namespace Ripple.Treasury.Assessment.Services.Inputs;

public class CreateEventInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Venue { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
    public int TotalCapacity { get; set; }
    public List<PricingTierInput> PricingTiers { get; set; } = [];
}

public class PricingTierInput
{
    public string Name { get; set; } = string.Empty;
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
    public int Allocation { get; set; }
}
