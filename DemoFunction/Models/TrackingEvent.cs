namespace DemoFunction;

// Mirrors dbo.EventCategories (stored as TINYINT).
public enum EventCategory : byte
{
    Unknown = 0,
    Page = 1,
    User = 2,
    Commerce = 3,
    System = 4,
    Error = 5,
}

public enum EventSeverity : byte
{
    Trace = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4,
}

public enum EventStatus : byte
{
    Received = 0,
    Processed = 1,
    Failed = 2,
    DeadLettered = 3,
}

// The tracking event carried on the "tracking.event" topic and persisted to SQL.
public record TrackingEvent
{
    // Business key — stable across systems, distinct from the DB identity.
    public Guid EventId { get; init; } = Guid.NewGuid();

    public string EventName { get; init; } = string.Empty;

    public EventCategory Category { get; init; } = EventCategory.Unknown;

    public EventSeverity Severity { get; init; } = EventSeverity.Information;

    public string? UserId { get; init; }

    public string? SessionId { get; init; }

    public string? CorrelationId { get; init; }

    public string? Source { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    // Free-form JSON blob (validated by an ISJSON constraint in the DB).
    public string? Payload { get; init; }

    // When the event actually occurred (maps to OccurredAt).
    public DateTimeOffset Timestamp { get; init; }

    // Arbitrary key/value metadata -> dbo.EventProperties (child rows).
    public Dictionary<string, string?>? Properties { get; init; }
}
