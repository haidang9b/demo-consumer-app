namespace DemoFunction.Data;

public interface ITrackingEventRepository
{
    Task InsertAsync(TrackingEvent trackingEvent, CancellationToken cancellationToken = default);

    Task InsertManyAsync(IReadOnlyCollection<TrackingEvent> trackingEvents, CancellationToken cancellationToken = default);
}
