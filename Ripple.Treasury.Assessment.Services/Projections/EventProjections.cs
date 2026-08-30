namespace Ripple.Treasury.Assessment.Services.Projections;

public class EventSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
    public int TotalCapacity { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class EventDetail
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Venue { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
    public int TotalCapacity { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PricingTierDetail> PricingTiers { get; set; } = [];
}

public class PricingTierDetail
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
    public int Allocation { get; set; }
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

public class TierAvailability
{
    public Guid PricingTierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
    public int Allocation { get; set; }
    public int Sold { get; set; }
    public int Available { get; set; }
}

public class EventAvailability
{
    public Guid EventId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalAvailable { get; set; }
    public List<TierAvailability> PricingTiers { get; set; } = [];
}
