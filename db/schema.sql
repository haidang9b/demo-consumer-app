-- =============================================================================
--  TrackingDb schema
--  Mirrored by TrackingEventRepository.EnsureSchemaAsync (auto-created on first
--  insert). Run this manually if you prefer to provision the DB up front:
--    docker exec -i sqledge /opt/mssql-tools/bin/sqlcmd \
--      -S localhost -U sa -P 'Your_password123!' -C -i /path/schema.sql
-- =============================================================================

IF DB_ID(N'TrackingDb') IS NULL
    CREATE DATABASE [TrackingDb];
GO

USE [TrackingDb];
GO

-- ---------------------------------------------------------------------------
-- Lookup: event categories (mirrors the EventCategory enum in code)
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.EventCategories', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.EventCategories
    (
        Id   TINYINT       NOT NULL CONSTRAINT PK_EventCategories PRIMARY KEY,
        Name NVARCHAR(50)  NOT NULL CONSTRAINT UQ_EventCategories_Name UNIQUE
    );

    INSERT INTO dbo.EventCategories (Id, Name) VALUES
        (0, N'Unknown'),
        (1, N'Page'),
        (2, N'User'),
        (3, N'Commerce'),
        (4, N'System'),
        (5, N'Error');
END;
GO

-- ---------------------------------------------------------------------------
-- Main: tracking events
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.TrackingEvents', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TrackingEvents
    (
        Id             BIGINT           IDENTITY(1,1) NOT NULL,
        EventId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_TrackingEvents_EventId  DEFAULT NEWID(),
        EventName      NVARCHAR(200)    NOT NULL,
        Category       TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Category  DEFAULT (0),
        Severity       TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Severity  DEFAULT (1),
        Status         TINYINT          NOT NULL CONSTRAINT DF_TrackingEvents_Status    DEFAULT (0),
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
GO

-- ---------------------------------------------------------------------------
-- Child: arbitrary key/value properties (1 event -> many properties)
-- ---------------------------------------------------------------------------
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
GO

-- ---------------------------------------------------------------------------
-- Table types (used by the repository's bulk InsertManyAsync via TVPs)
-- ---------------------------------------------------------------------------
IF TYPE_ID('dbo.TrackingEventTableType') IS NULL
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
);
GO

IF TYPE_ID('dbo.EventPropertyTableType') IS NULL
CREATE TYPE dbo.EventPropertyTableType AS TABLE
(
    EventId UNIQUEIDENTIFIER NOT NULL,
    [Key]   NVARCHAR(100)    NOT NULL,
    [Value] NVARCHAR(MAX)    NULL
);
GO
