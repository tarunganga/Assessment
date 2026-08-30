namespace Ripple.Treasury.Assessment.Services.Exceptions;

public class PurchaseNotFoundException : Exception
{
    public PurchaseNotFoundException(Guid purchaseId)
        : base($"Purchase '{purchaseId}' was not found.")
    {
        PurchaseId = purchaseId;
    }

    public Guid PurchaseId { get; }
}
