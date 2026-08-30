using Microsoft.AspNetCore.Mvc;
using Ripple.Treasury.Assessment.Api.Mapping;
using Ripple.Treasury.Assessment.Api.Models.Requests;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.Api.Controllers;

[ApiController]
[Route("events")]
[Produces("application/json")]
public class EventsController(IEventService events, ITicketPurchaseService purchases) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(EventDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateEventAsync(
        [FromBody] SaveEventRequest request, CancellationToken cancellationToken)
    {
        Guid eventId = await events.CreateAsync(request.ToCreateInput(), cancellationToken);
        EventDetail created = await events.GetAsync(eventId, cancellationToken);

        return CreatedAtAction(nameof(GetEventByIdAsync), new { eventId }, created);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<EventSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllEventsAsync(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] string? venue,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 100 ? 20 : pageSize;

        return Ok(await events.ListAsync(from, venue, page, pageSize, cancellationToken));
    }

    [HttpGet("{eventId:guid}")]
    [ProducesResponseType(typeof(EventDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEventByIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        return Ok(await events.GetAsync(eventId, cancellationToken));
    }

    [HttpPut("{eventId:guid}")]
    [ProducesResponseType(typeof(EventDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateEventAsync(
        Guid eventId, [FromBody] SaveEventRequest request, CancellationToken cancellationToken)
    {
        await events.UpdateAsync(eventId, request.ToUpdateInput(), cancellationToken);

        return Ok(await events.GetAsync(eventId, cancellationToken));
    }

    [HttpDelete("{eventId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await events.DeleteAsync(eventId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{eventId:guid}/publish")]
    [ProducesResponseType(typeof(EventDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PublishEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await events.PublishAsync(eventId, cancellationToken);

        return Ok(await events.GetAsync(eventId, cancellationToken));
    }

    [HttpGet("{eventId:guid}/availability")]
    [ProducesResponseType(typeof(EventAvailability), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEventAvailabilityAsync(Guid eventId, CancellationToken cancellationToken)
    {
        return Ok(await events.GetAvailabilityAsync(eventId, cancellationToken));
    }

    [HttpGet("{eventId:guid}/sales-report")]
    [ProducesResponseType(typeof(SalesReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEventSalesReportAsync(Guid eventId, CancellationToken cancellationToken)
    {
        return Ok(await events.GetSalesReportAsync(eventId, cancellationToken));
    }

    [HttpPost("{eventId:guid}/purchases")]
    [ProducesResponseType(typeof(PurchaseDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PurchaseDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PurchaseTicketsAsync(
        Guid eventId,
        [FromBody] PurchaseTicketsRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            ModelState.AddModelError(IdempotencyKeyHeader, "The Idempotency-Key header is required.");
            return ValidationProblem(ModelState);
        }

        PurchaseResult result = await purchases.PurchaseAsync(
            request.ToInput(eventId, idempotencyKey), cancellationToken);

        PurchaseDetail detail = await purchases.GetAsync(result.PurchaseId, cancellationToken);

        if (!result.IsReplay)
        {
            return CreatedAtAction(
                nameof(PurchasesController.GetPurchaseByIdAsync), "Purchases", new { purchaseId = detail.Id }, detail);
        }

        Response.Headers["Idempotent-Replay"] = "true";
        return Ok(detail);
    }

    private const string IdempotencyKeyHeader = "Idempotency-Key";
}
