namespace Ripple.Treasury.Assessment.Services;

public class PurchaseResult
{
    public Guid PurchaseId { get; set; }

    // True when this key was already used with an identical request
    public bool IsReplay { get; set; }
}
