namespace Ripple.Treasury.Assessment.Services.Inputs;

public class UpdateEventInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Venue { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
    public int TotalCapacity { get; set; }
    public List<PricingTierInput> PricingTiers { get; set; } = [];
}
