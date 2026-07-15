using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DemoFunction;

// Multi-output: publishes a collection to the topic AND returns an HTTP response.
public class PublishOutput
{
    [ServiceBusOutput("tracking.event", Connection = "ServiceBusConnection")]
    public string[] Messages { get; set; } = [];

    [HttpResult]
    public IActionResult HttpResponse { get; set; } = new OkResult();
}

public class PublishTrackingEvents
{
    private const int MaxMessages = 10_000;

    private readonly ILogger<PublishTrackingEvents> _logger;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public PublishTrackingEvents(ILogger<PublishTrackingEvents> logger)
    {
        _logger = logger;
    }

    // Publishes N messages to the "tracking.event" topic.
    // The caller chooses N via the "count" query string, e.g.:
    //   POST /api/tracking-events?count=100
    // Defaults to 1 when omitted.
    [Function(nameof(PublishTrackingEvents))]
    public PublishOutput Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tracking-events")] HttpRequest req)
    {
        var count = 1;
        if (req.Query.TryGetValue("count", out var rawCount) &&
            int.TryParse(rawCount, out var parsedCount))
        {
            count = parsedCount;
        }

        if (count < 1 || count > MaxMessages)
        {
            return new PublishOutput
            {
                HttpResponse = new BadRequestObjectResult($"count must be between 1 and {MaxMessages}."),
            };
        }

        var timestamp = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid().ToString("N");

        var messages = Enumerable.Range(1, count)
            .Select(i => JsonSerializer.Serialize(
                new TrackingEvent
                {
                    EventName = $"event_{i}",
                    Category = (EventCategory)(i % 6),
                    Severity = EventSeverity.Information,
                    UserId = $"user_{i}",
                    SessionId = Guid.NewGuid().ToString("N"),
                    CorrelationId = correlationId,
                    Source = "http-publisher",
                    Payload = $"{{\"index\":{i}}}",
                    Timestamp = timestamp,
                    Properties = new Dictionary<string, string?>
                    {
                        ["index"] = i.ToString(),
                        ["batch"] = correlationId,
                    },
                },
                JsonOptions))
            .ToArray();

        _logger.LogInformation("Publishing {Count} tracking events to 'tracking.event'.", count);

        return new PublishOutput
        {
            Messages = messages,
            HttpResponse = new OkObjectResult(new { published = count }),
        };
    }
}
