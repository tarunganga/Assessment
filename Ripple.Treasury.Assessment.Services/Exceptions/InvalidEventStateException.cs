namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class InvalidEventStateException : Exception
{
    public InvalidEventStateException(Guid eventId, string currentStatus, string attempted)
        : base($"Event '{eventId}' is {currentStatus}; {attempted} is not allowed.")
    {
        EventId = eventId;
        CurrentStatus = currentStatus;
        Attempted = attempted;
    }

    public Guid EventId { get; }
    public string CurrentStatus { get; }
    public string Attempted { get; }
}
