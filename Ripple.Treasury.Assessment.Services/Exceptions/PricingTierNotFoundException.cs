namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class PricingTierNotFoundException : Exception
{
    public PricingTierNotFoundException(Guid eventId, Guid pricingTierId)
        : base($"Pricing tier '{pricingTierId}' does not belong to event '{eventId}'.")
    {
        EventId = eventId;
        PricingTierId = pricingTierId;
    }

    public Guid EventId { get; }
    public Guid PricingTierId { get; }
}
