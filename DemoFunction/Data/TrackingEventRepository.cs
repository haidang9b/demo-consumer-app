using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DemoFunction.Data;

public class TrackingEventRepository : ITrackingEventRepository
{
    private readonly string _connectionString;

    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static bool _schemaReady;

    public TrackingEventRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetValue<string>("SqlConnection")
            ?? throw new InvalidOperationException("Missing 'SqlConnection' configuration value.");
    }

    public async Task InsertAsync(TrackingEvent trackingEvent, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string insertEvent = """
            INSERT INTO dbo.TrackingEvents
                (EventId, EventName, Category, Severity, Status, UserId, SessionId,
                 CorrelationId, Source, IpAddress, UserAgent, Payload, OccurredAt, ProcessedAtUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@EventId, @EventName, @Category, @Severity, @Status, @UserId, @SessionId,
                 @CorrelationId, @Source, @IpAddress, @UserAgent, @Payload, @OccurredAt, SYSUTCDATETIME());
            """;

        var eventId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            insertEvent,
            new
            {
                trackingEvent.EventId,
                trackingEvent.EventName,
                Category = (byte)trackingEvent.Category,
                Severity = (byte)trackingEvent.Severity,
                Status = (byte)EventStatus.Processed,
                trackingEvent.UserId,
                trackingEvent.SessionId,
                trackingEvent.CorrelationId,
                trackingEvent.Source,
                trackingEvent.IpAddress,
                trackingEvent.UserAgent,
                trackingEvent.Payload,
                OccurredAt = trackingEvent.Timestamp,
            },
            transaction,
            cancellationToken: cancellationToken));

        if (trackingEvent.Properties is { Count: > 0 })
        {
            const string insertProperty = """
                INSERT INTO dbo.EventProperties (TrackingEventId, [Key], [Value])
                VALUES (@TrackingEventId, @Key, @Value);
                """;

            foreach (var (key, value) in trackingEvent.Properties)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    insertProperty,
                    new { TrackingEventId = eventId, Key = key, Value = value },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertManyAsync(IReadOnlyCollection<TrackingEvent> trackingEvents, CancellationToken cancellationToken = default)
    {
        if (trackingEvents.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);

        var eventsTable = BuildEventsTable(trackingEvents);
        var propertiesTable = BuildPropertiesTable(trackingEvents);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo.TrackingEvents
                (EventId, EventName, Category, Severity, Status, UserId, SessionId,
                 CorrelationId, Source, IpAddress, UserAgent, Payload, OccurredAt, ProcessedAtUtc)
            SELECT
                e.EventId, e.EventName, e.Category, e.Severity, e.Status, e.UserId, e.SessionId,
                e.CorrelationId, e.Source, e.IpAddress, e.UserAgent, e.Payload, e.OccurredAt, SYSUTCDATETIME()
            FROM @Events e
            WHERE NOT EXISTS (SELECT 1 FROM dbo.TrackingEvents t WHERE t.EventId = e.EventId);

            INSERT INTO dbo.EventProperties (TrackingEventId, [Key], [Value])
            SELECT te.Id, p.[Key], p.[Value]
            FROM @Props p
            JOIN dbo.TrackingEvents te ON te.EventId = p.EventId
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.EventProperties ep
                WHERE ep.TrackingEventId = te.Id AND ep.[Key] = p.[Key]);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Events = eventsTable.AsTableValuedParameter("dbo.TrackingEventTableType"),
                Props = propertiesTable.AsTableValuedParameter("dbo.EventPropertyTableType"),
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    // TVP column order MUST match the CREATE TYPE definitions (matched by ordinal).
    private static DataTable BuildEventsTable(IEnumerable<TrackingEvent> events)
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(Guid));
        table.Columns.Add("EventName", typeof(string));
        table.Columns.Add("Category", typeof(byte));
        table.Columns.Add("Severity", typeof(byte));
        table.Columns.Add("Status", typeof(byte));
        table.Columns.Add("UserId", typeof(string));
        table.Columns.Add("SessionId", typeof(string));
        table.Columns.Add("CorrelationId", typeof(string));
        table.Columns.Add("Source", typeof(string));
        table.Columns.Add("IpAddress", typeof(string));
        table.Columns.Add("UserAgent", typeof(string));
        table.Columns.Add("Payload", typeof(string));
        table.Columns.Add("OccurredAt", typeof(DateTimeOffset));

        foreach (var e in events)
        {
            table.Rows.Add(
                e.EventId,
                e.EventName,
                (byte)e.Category,
                (byte)e.Severity,
                (byte)EventStatus.Processed,
                (object?)e.UserId ?? DBNull.Value,
                (object?)e.SessionId ?? DBNull.Value,
                (object?)e.CorrelationId ?? DBNull.Value,
                (object?)e.Source ?? DBNull.Value,
                (object?)e.IpAddress ?? DBNull.Value,
                (object?)e.UserAgent ?? DBNull.Value,
                (object?)e.Payload ?? DBNull.Value,
                e.Timestamp);
        }

        return table;
    }

    private static DataTable BuildPropertiesTable(IEnumerable<TrackingEvent> events)
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(Guid));
        table.Columns.Add("Key", typeof(string));
        table.Columns.Add("Value", typeof(string));

        foreach (var e in events)
        {
            if (e.Properties is null)
            {
                continue;
            }

            foreach (var (key, value) in e.Properties)
            {
                table.Rows.Add(e.EventId, key, (object?)value ?? DBNull.Value);
            }
        }

        return table;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await SchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            var builder = new SqlConnectionStringBuilder(_connectionString);
            var targetDatabase = builder.InitialCatalog;

            // 1. Create the database if it does not exist (connect to master first).
            builder.InitialCatalog = "master";
            await using (var master = new SqlConnection(builder.ConnectionString))
            {
                await master.OpenAsync(cancellationToken);
                await master.ExecuteAsync(
                    $"IF DB_ID(N'{targetDatabase}') IS NULL CREATE DATABASE [{targetDatabase}];");
            }

            // 2. Create the tables/indexes if they do not exist (mirrors db/schema.sql).
            await using (var db = new SqlConnection(_connectionString))
            {
                await db.OpenAsync(cancellationToken);
                foreach (var batch in SchemaBatches)
                {
                    await db.ExecuteAsync(new CommandDefinition(batch, cancellationToken: cancellationToken));
                }
            }

            _schemaReady = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    // Each entry is one batch (executed separately because CREATE INDEX etc. reference
    // objects created earlier). Mirrors db/schema.sql.
    private static readonly string[] SchemaBatches =
    [
        """
        IF OBJECT_ID('dbo.EventCategories', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.EventCategories
            (
                Id   TINYINT      NOT NULL CONSTRAINT PK_EventCategories PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL CONSTRAINT UQ_EventCategories_Name UNIQUE
            );

            INSERT INTO dbo.EventCategories (Id, Name) VALUES
                (0, N'Unknown'), (1, N'Page'), (2, N'User'),
                (3, N'Commerce'), (4, N'System'), (5, N'Error');
        END;
        """,
        """
        IF OBJECT_ID('dbo.TrackingEvents', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.TrackingEvents
            (
                Id             BIGINT           IDENTITY(1,1) NOT NULL,
                EventId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_TrackingEvents_EventId   DEFAULT NEWID(),
                EventName      NVARCHAR(200)    NOT NULL,
                Category       TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Category   DEFAULT (0),
                Severity       TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Severity   DEFAULT (1),
                Status         TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Status     DEFAULT (0),
                UserId         NVARCHAR(200)    NULL,
                SessionId      NVARCHAR(100)    NULL,
                CorrelationId  NVARCHAR(100)    NULL,
                Source         NVARCHAR(100)    NULL,
                IpAddress      NVARCHAR(45)     NULL,
                UserAgent      NVARCHAR(512)    NULL,
                Payload        NVARCHAR(MAX)    NULL,
                OccurredAt     DATETIMEOFFSET   NOT NULL,
                ReceivedAtUtc  DATETIME2(3)     NOT NULL CONSTRAINT DF_TrackingEvents_ReceivedAt DEFAULT SYSUTCDATETIME(),
                ProcessedAtUtc DATETIME2(3)     NULL,
                RowVersion     ROWVERSION       NOT NULL,

                CONSTRAINT PK_TrackingEvents          PRIMARY KEY CLUSTERED (Id),
                CONSTRAINT UQ_TrackingEvents_EventId  UNIQUE (EventId),
                CONSTRAINT CK_TrackingEvents_Payload  CHECK (Payload IS NULL OR ISJSON(Payload) = 1),
                CONSTRAINT FK_TrackingEvents_Category FOREIGN KEY (Category) REFERENCES dbo.EventCategories (Id)
            );

            CREATE INDEX IX_TrackingEvents_OccurredAt ON dbo.TrackingEvents (OccurredAt DESC);
            CREATE INDEX IX_TrackingEvents_EventName  ON dbo.TrackingEvents (EventName);
            CREATE INDEX IX_TrackingEvents_UserId     ON dbo.TrackingEvents (UserId) WHERE UserId IS NOT NULL;
            CREATE INDEX IX_TrackingEvents_Status     ON dbo.TrackingEvents (Status) INCLUDE (EventName, OccurredAt);
        END;
        """,
        """
        IF OBJECT_ID('dbo.EventProperties', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.EventProperties
            (
                Id              BIGINT        IDENTITY(1,1) NOT NULL,
                TrackingEventId BIGINT        NOT NULL,
                [Key]           NVARCHAR(100) NOT NULL,
                [Value]         NVARCHAR(MAX) NULL,

                CONSTRAINT PK_EventProperties       PRIMARY KEY CLUSTERED (Id),
                CONSTRAINT FK_EventProperties_Event FOREIGN KEY (TrackingEventId)
                    REFERENCES dbo.TrackingEvents (Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX UX_EventProperties_Event_Key ON dbo.EventProperties (TrackingEventId, [Key]);
        END;
        """,
        """
        IF TYPE_ID('dbo.TrackingEventTableType') IS NULL
        EXEC('
        CREATE TYPE dbo.TrackingEventTableType AS TABLE
        (
            EventId       UNIQUEIDENTIFIER NOT NULL,
            EventName     NVARCHAR(200)    NOT NULL,
            Category      TINYINT          NOT NULL,
            Severity      TINYINT          NOT NULL,
            Status        TINYINT          NOT NULL,
            UserId        NVARCHAR(200)    NULL,
            SessionId     NVARCHAR(100)    NULL,
            CorrelationId NVARCHAR(100)    NULL,
            Source        NVARCHAR(100)    NULL,
            IpAddress     NVARCHAR(45)     NULL,
            UserAgent     NVARCHAR(512)    NULL,
            Payload       NVARCHAR(MAX)    NULL,
            OccurredAt    DATETIMEOFFSET   NOT NULL
        );');
        """,
        """
        IF TYPE_ID('dbo.EventPropertyTableType') IS NULL
        EXEC('
        CREATE TYPE dbo.EventPropertyTableType AS TABLE
        (
            EventId UNIQUEIDENTIFIER NOT NULL,
            [Key]   NVARCHAR(100)    NOT NULL,
            [Value] NVARCHAR(MAX)    NULL
        );');
        """,
    ];
}
