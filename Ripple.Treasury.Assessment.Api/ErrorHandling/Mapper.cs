using Microsoft.AspNetCore.Mvc;
using Ripple.Treasury.Assessment.Services.Exceptions;

namespace Ripple.Treasury.Assessment.Api.ErrorHandling;

/// <summary>
/// Provides extension methods for mapping exceptions to <see cref="ProblemDetails"/> objects.
/// </summary>
public static class Mapper
{
    public static ProblemDetails ToProblemDetails(this Exception exception)
    {
        switch (exception)
        {
            case EventNotFoundException notFound:
            {
                ProblemDetails problem = new()
                {
                    Status = StatusCodes.Status404NotFound,
                    Detail = notFound.Message,
                    Extensions =
                    {
                        ["eventId"] = notFound.EventId
                    }
                };
                return problem;
            }

            case InsufficientInventoryException inventory:
            {
                ProblemDetails problem = new()
                {
                    Title = "Insufficient inventory",
                    Status = StatusCodes.Status409Conflict,
                    Detail = inventory.Message,
                    Extensions =
                    {
                        ["pricingTierId"] = inventory.PricingTierId,
                        ["requested"] = inventory.Requested,
                        ["available"] = inventory.Available
                    }
                };
                return problem;
            }

            case CapacityViolationException capacity:
            {
                ProblemDetails problem = new()
                {
                    Title = "Capacity violation",
                    Status = StatusCodes.Status409Conflict,
                    Detail = capacity.Message,
                    Extensions =
                    {
                        ["eventId"] = capacity.EventId
                    }
                };
                return problem;
            }

            case IdempotencyKeyConflictException idempotency:
            {
                ProblemDetails problem = new()
                {
                    Title = "Idempotency key conflict",
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Detail = idempotency.Message,
                    Extensions =
                    {
                        ["idempotencyKey"] = idempotency.IdempotencyKey
                    }
                };
                return problem;
            }

            case InvalidEventStateException invalidState:
            {
                ProblemDetails problem = new()
                {
                    Title = "Invalid event state",
                    Status = StatusCodes.Status409Conflict,
                    Detail = invalidState.Message,
                    Extensions =
                    {
                        ["eventId"] = invalidState.EventId,
                        ["currentStatus"] = invalidState.CurrentStatus
                    }
                };
                return problem;
            }

            case PricingTierNotFoundException tierNotFound:
            {
                ProblemDetails problem = new()
                {
                    Status = StatusCodes.Status404NotFound,
                    Detail = tierNotFound.Message,
                    Extensions =
                    {
                        ["eventId"] = tierNotFound.EventId,
                        ["pricingTierId"] = tierNotFound.PricingTierId
                    }
                };
                return problem;
            }

            case PurchaseNotFoundException purchaseNotFound:
            {
                ProblemDetails problem = new()
                {
                    Status = StatusCodes.Status404NotFound,
                    Detail = purchaseNotFound.Message,
                    Extensions =
                    {
                        ["purchaseId"] = purchaseNotFound.PurchaseId
                    }
                };
                return problem;
            }

            default:
            {
                return new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = "The request could not be completed"
                };
            }
        }
    }
}