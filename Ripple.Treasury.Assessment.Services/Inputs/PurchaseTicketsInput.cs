namespace Ripple.Treasury.Assessment.Services.Inputs;

public class PurchaseTicketsInput
{
    public Guid EventId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PurchaserEmail { get; set; } = string.Empty;
    public List<PurchaseItemInput> PurchaseItems { get; set; } = [];
}

public class PurchaseItemInput
{
    public Guid PricingTierId { get; set; }
    public int Quantity { get; set; }
}
