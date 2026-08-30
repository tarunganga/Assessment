using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.Services;

public interface ITicketPurchaseService
{
    Task<PurchaseResult> PurchaseAsync(PurchaseTicketsInput input, CancellationToken cancellationToken);

    Task<PurchaseDetail> GetAsync(Guid purchaseId, CancellationToken cancellationToken);
}
