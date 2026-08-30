namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class EventNotFoundException(Guid eventId) : Exception($"Event '{eventId}' was not found.")
{
    public Guid EventId { get; } = eventId;
}
