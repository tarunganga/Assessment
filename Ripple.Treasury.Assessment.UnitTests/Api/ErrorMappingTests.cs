using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ripple.Treasury.Assessment.Api.ErrorHandling;
using Ripple.Treasury.Assessment.Services.Exceptions;

namespace Ripple.Treasury.Assessment.UnitTests.Api;

public class ErrorMappingTests
{
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid TierId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid PurchaseId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");

    [Fact]
    public void A_missing_event_is_a_404_carrying_the_id()
    {
        ProblemDetails problem = new EventNotFoundException(EventId).ToProblemDetails();

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal(EventId, problem.Extensions["eventId"]);
    }

    [Fact]
    public void A_missing_pricing_tier_is_a_404_naming_both_ids()
    {
        ProblemDetails problem = new PricingTierNotFoundException(EventId, TierId).ToProblemDetails();

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal(EventId, problem.Extensions["eventId"]);
        Assert.Equal(TierId, problem.Extensions["pricingTierId"]);
    }

    [Fact]
    public void A_missing_purchase_is_a_404()
    {
        ProblemDetails problem = new PurchaseNotFoundException(PurchaseId).ToProblemDetails();

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal(PurchaseId, problem.Extensions["purchaseId"]);
    }

    [Fact]
    public void Running_out_of_inventory_is_a_409_saying_how_many_were_left()
    {
        ProblemDetails problem = new InsufficientInventoryException(TierId, 5, 2).ToProblemDetails();

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Insufficient inventory", problem.Title);
        Assert.Equal(TierId, problem.Extensions["pricingTierId"]);
        Assert.Equal(5, problem.Extensions["requested"]);
        Assert.Equal(2, problem.Extensions["available"]);
    }

    [Fact]
    public void A_capacity_violation_is_a_409()
    {
        ProblemDetails problem =
            new CapacityViolationException(EventId, "Tier allocations total 90 but capacity is 100.")
                .ToProblemDetails();

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Capacity violation", problem.Title);
        Assert.Equal(EventId, problem.Extensions["eventId"]);
    }

    [Fact]
    public void An_invalid_event_state_is_a_409_reporting_the_state_it_found()
    {
        ProblemDetails problem =
            new InvalidEventStateException(EventId, "Cancelled", "purchasing").ToProblemDetails();

        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Invalid event state", problem.Title);
        Assert.Equal("Cancelled", problem.Extensions["currentStatus"]);
    }

    [Fact]
    public void An_idempotency_conflict_is_a_422_and_not_a_409()
    {
        ProblemDetails problem = new IdempotencyKeyConflictException("order-100").ToProblemDetails();

        // A reused key with a different body is the caller's mistake to fix,
        // not a race they can retry through - so it must not look like a conflict.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.Status);
        Assert.Equal("order-100", problem.Extensions["idempotencyKey"]);
    }

    [Fact]
    public void An_unrecognised_exception_is_a_500_that_leaks_nothing()
    {
        ProblemDetails problem =
            new InvalidOperationException("connection string password=hunter2").ToProblemDetails();

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.Status);
        Assert.Equal("The request could not be completed", problem.Detail);
        Assert.DoesNotContain("hunter2", problem.Detail);
        Assert.Empty(problem.Extensions);
    }

    [Fact]
    public void Every_domain_exception_maps_to_something_other_than_500()
    {
        // A new domain exception without a case here would fall through to 500.
        List<Exception> domainErrors =
        [
            new EventNotFoundException(EventId),
            new PricingTierNotFoundException(EventId, TierId),
            new PurchaseNotFoundException(PurchaseId),
            new InsufficientInventoryException(TierId, 5, 2),
            new CapacityViolationException(EventId, "capacity"),
            new InvalidEventStateException(EventId, "Draft", "purchasing"),
            new IdempotencyKeyConflictException("order-100")
        ];

        Assert.All(domainErrors, error =>
            Assert.NotEqual(StatusCodes.Status500InternalServerError, error.ToProblemDetails().Status));
    }

    [Fact]
    public void Every_mapped_problem_repeats_the_exception_message_as_the_detail()
    {
        CapacityViolationException error = new(EventId, "Tier allocations total 90 but capacity is 100.");

        Assert.Equal(error.Message, error.ToProblemDetails().Detail);
    }
}
