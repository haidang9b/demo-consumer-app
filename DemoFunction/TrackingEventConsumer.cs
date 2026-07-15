using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DemoFunction.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DemoFunction;

// Consumes messages published to the "tracking.event" topic via the "demoapp" subscription.
// Batch dispatch: the runtime delivers up to host.json's maxMessageBatchSize messages per call.
// Manual settlement (autoCompleteMessages = false) lets us:
//   - dead-letter poison/unprocessable messages immediately (no point retrying),
//   - bulk-insert the valid ones in a single transaction, then complete them, and
//   - abandon on transient failure so Service Bus redelivers (and eventually dead-letters
//     after MaxDeliveryCount).
public class TrackingEventConsumer
{
    private readonly ILogger<TrackingEventConsumer> _logger;
    private readonly ITrackingEventRepository _repository;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public TrackingEventConsumer(ILogger<TrackingEventConsumer> logger, ITrackingEventRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    [Function(nameof(TrackingEventConsumer))]
    public async Task Run(
        [ServiceBusTrigger(
            topicName: "tracking.event",
            subscriptionName: "demoapp",
            Connection = "ServiceBusConnection",
            IsBatched = true)]
        ServiceBusReceivedMessage[] messages,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received batch of {Count} tracking events.", messages.Length);

        var toPersist = new List<TrackingEvent>(messages.Length);
        var toComplete = new List<ServiceBusReceivedMessage>(messages.Length);

        foreach (var message in messages)
        {
            var body = message.Body.ToString();

            // --- Non-retryable (poison) messages -> dead-letter immediately ----------------
            TrackingEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<TrackingEvent>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed tracking event {MessageId}; dead-lettering.", message.MessageId);
                await messageActions.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: "DeserializationError",
                    deadLetterErrorDescription: ex.Message,
                    cancellationToken: cancellationToken);
                continue;
            }

            if (evt is null || string.IsNullOrWhiteSpace(evt.EventName))
            {
                await messageActions.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: "ValidationError",
                    deadLetterErrorDescription: "EventName is required.",
                    cancellationToken: cancellationToken);
                continue;
            }

            // --- Demo hook: simulate a transient failure -> abandon this one for retry ------
            if (string.Equals(evt.EventName, "transient-fail", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Simulated transient failure for {MessageId}; abandoning for retry.", message.MessageId);
                await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                continue;
            }

            toPersist.Add(evt);
            toComplete.Add(message);
        }

        if (toPersist.Count == 0)
        {
            return;
        }

        // --- Bulk-insert the valid messages, then settle them ------------------------------
        try
        {
            await _repository.InsertManyAsync(toPersist, cancellationToken);
        }
        catch (Exception ex)
        {
            // Persist failed for the batch: abandon so the whole set is redelivered/retried.
            _logger.LogError(ex, "Bulk insert failed for {Count} events; abandoning batch for retry.", toPersist.Count);
            foreach (var message in toComplete)
            {
                await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
            }
            return;
        }

        foreach (var message in toComplete)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }

        _logger.LogInformation("Persisted and completed {Count} tracking events.", toPersist.Count);
    }
}
