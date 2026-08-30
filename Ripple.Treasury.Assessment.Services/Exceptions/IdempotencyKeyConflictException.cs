namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class IdempotencyKeyConflictException(string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' was already used with a different request body.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}
