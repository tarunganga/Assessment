using Microsoft.AspNetCore.Mvc;
using Ripple.Treasury.Assessment.Services.Projections;
using Ripple.Treasury.Assessment.Services;

namespace Ripple.Treasury.Assessment.Api.Controllers;

[ApiController]
[Route("purchases")]
[Produces("application/json")]
public class PurchasesController(ITicketPurchaseService purchases) : ControllerBase
{
    [HttpGet("{purchaseId:guid}")]
    [ProducesResponseType(typeof(PurchaseDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPurchaseByIdAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        return Ok(await purchases.GetAsync(purchaseId, cancellationToken));
    }
}
