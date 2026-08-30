namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class CapacityViolationException(Guid eventId, string reason) : Exception(reason)
{
    public Guid EventId { get; } = eventId;
}
