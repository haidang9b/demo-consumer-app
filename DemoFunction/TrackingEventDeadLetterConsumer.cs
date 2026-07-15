using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DemoFunction;

// Processes messages that landed in the dead-letter queue (DLQ) of the "demoapp" subscription.
// The DLQ is a built-in sub-queue addressed as "<subscription>/$DeadLetterQueue".
// Messages arrive here when they are explicitly dead-lettered or exceed MaxDeliveryCount.
public class TrackingEventDeadLetterConsumer
{
    private readonly ILogger<TrackingEventDeadLetterConsumer> _logger;

    public TrackingEventDeadLetterConsumer(ILogger<TrackingEventDeadLetterConsumer> logger)
    {
        _logger = logger;
    }

    [Function(nameof(TrackingEventDeadLetterConsumer))]
    public async Task Run(
        [ServiceBusTrigger(
            topicName: "tracking.event",
            subscriptionName: "demoapp/$DeadLetterQueue",
            Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "DEAD-LETTER: MessageId {MessageId} | Reason: {Reason} | Description: {Description} | Body: {Body}",
            message.MessageId,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.Body.ToString());

        // Handle the dead-lettered message here: persist for inspection, alert, or attempt a
        // repair + republish to the topic. For the demo we log it and complete so the DLQ drains.
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }
}
