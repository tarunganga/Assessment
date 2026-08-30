namespace Ripple.Treasury.Assessment.Services.Projections;

public class PurchaseDetail
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string PurchaserEmail { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<PurchaseItemDetail> Items { get; set; } = [];
    public List<Guid> TicketIds { get; set; } = [];
}

public class PurchaseItemDetail
{
    public Guid PricingTierId { get; set; }
    public string PricingTierName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ItemTotal { get; set; }
}

public class SalesReport
{
    public Guid EventId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public int TicketsSold { get; set; }
    public int TicketsAvailable { get; set; }
    public decimal TotalRevenue { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int PurchaseCount { get; set; }
    public List<TierSales> PricingTiers { get; set; } = [];
}

public class TierSales
{
    public Guid PricingTierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceAmount { get; set; }
    public int Allocation { get; set; }
    public int Sold { get; set; }
    public int Available { get; set; }
    public decimal Revenue { get; set; }
}
