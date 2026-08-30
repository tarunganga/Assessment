using Ripple.Treasury.Assessment.Api.Models.Requests;
using Ripple.Treasury.Assessment.Services.Inputs;

namespace Ripple.Treasury.Assessment.Api.Mapping;

public static class RequestMapping
{
    public static CreateEventInput ToCreateInput(this SaveEventRequest request)
    {
        CreateEventInput input = new()
        {
            Name = request.Name,
            Description = request.Description,
            Venue = request.Venue,
            StartsAtUtc = request.StartsAtUtc,
            TotalCapacity = request.TotalCapacity
        };

        foreach (SavePricingTierRequest tier in request.PricingTiers)
        {
            input.PricingTiers.Add(ToTierInput(tier));
        }

        return input;
    }

    public static UpdateEventInput ToUpdateInput(this SaveEventRequest request)
    {
        UpdateEventInput input = new()
        {
            Name = request.Name,
            Description = request.Description,
            Venue = request.Venue,
            StartsAtUtc = request.StartsAtUtc,
            TotalCapacity = request.TotalCapacity
        };

        foreach (SavePricingTierRequest tier in request.PricingTiers)
        {
            input.PricingTiers.Add(ToTierInput(tier));
        }

        return input;
    }

    public static PurchaseTicketsInput ToInput(
        this PurchaseTicketsRequest request, Guid eventId, string idempotencyKey)
    {
        PurchaseTicketsInput input = new()
        {
            EventId = eventId,
            IdempotencyKey = idempotencyKey,
            PurchaserEmail = request.PurchaserEmail
        };

        foreach (PurchaseItemRequest item in request.PurchaseItems)
        {
            input.PurchaseItems.Add(new PurchaseItemInput
            {
                PricingTierId = item.PricingTierId,
                Quantity = item.Quantity
            });
        }

        return input;
    }

    private static PricingTierInput ToTierInput(SavePricingTierRequest tier)
    {
        return new PricingTierInput
        {
            Name = tier.Name,
            PriceAmount = tier.PriceAmount,
            PriceCurrency = tier.PriceCurrency,
            Allocation = tier.Allocation
        };
    }
}
