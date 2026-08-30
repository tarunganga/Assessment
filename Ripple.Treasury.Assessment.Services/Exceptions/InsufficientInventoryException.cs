namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class InsufficientInventoryException(Guid pricingTierId, int requested, int available)
    : Exception($"Pricing tier '{pricingTierId}' has {available} tickets available, {requested} were requested.")
{
    // Carries requested vs available so the response can tell the client how many
    // are actually left, rather than just a "sold out" message.

    public Guid PricingTierId { get; } = pricingTierId;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}
