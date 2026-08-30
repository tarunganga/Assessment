using Ripple.Treasury.Assessment.Api.Mapping;
using Ripple.Treasury.Assessment.Api.Models.Requests;
using Ripple.Treasury.Assessment.Services.Inputs;

namespace Ripple.Treasury.Assessment.UnitTests.Api;

public class RequestMappingTests
{
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid GaTier = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid VipTier = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    private static readonly DateTimeOffset StartsAt =
        new(2027, 6, 1, 19, 30, 0, TimeSpan.FromHours(2));

    private readonly SaveEventRequest _request;
    private readonly PurchaseTicketsRequest _purchase;

    // xUnit builds the class once per test, so a test may mutate the request freely.
    public RequestMappingTests()
    {
        _request = new SaveEventRequest
        {
            Name = "Opening Night",
            Description = "A show",
            Venue = "Royal Arena",
            StartsAtUtc = StartsAt,
            TotalCapacity = 100,
            PricingTiers =
            [
                new SavePricingTierRequest { Name = "GA", PriceAmount = 50.25m, PriceCurrency = "USD", Allocation = 60 },
                new SavePricingTierRequest { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
            ]
        };

        _purchase = new PurchaseTicketsRequest
        {
            PurchaserEmail = "buyer@example.com",
            PurchaseItems =
            [
                new PurchaseItemRequest { PricingTierId = GaTier, Quantity = 2 },
                new PurchaseItemRequest { PricingTierId = VipTier, Quantity = 1 }
            ]
        };
    }

    [Fact]
    public void Create_mapping_carries_every_field()
    {
        CreateEventInput input = _request.ToCreateInput();

        Assert.Equal("Opening Night", input.Name);
        Assert.Equal("A show", input.Description);
        Assert.Equal("Royal Arena", input.Venue);
        Assert.Equal(StartsAt, input.StartsAtUtc);
        Assert.Equal(100, input.TotalCapacity);
    }

    [Fact]
    public void Create_mapping_keeps_the_tiers_in_order_with_their_prices_intact()
    {
        CreateEventInput input = _request.ToCreateInput();

        Assert.Equal(["GA", "VIP"], input.PricingTiers.Select(t => t.Name));
        Assert.Equal(50.25m, input.PricingTiers[0].PriceAmount);
        Assert.Equal("USD", input.PricingTiers[0].PriceCurrency);
        Assert.Equal(60, input.PricingTiers[0].Allocation);
    }

    [Fact]
    public void Update_mapping_carries_the_same_fields_as_create()
    {
        CreateEventInput create = _request.ToCreateInput();
        UpdateEventInput update = _request.ToUpdateInput();

        // One request type feeds both, so the two must not drift apart.
        Assert.Equal(create.Name, update.Name);
        Assert.Equal(create.Description, update.Description);
        Assert.Equal(create.Venue, update.Venue);
        Assert.Equal(create.StartsAtUtc, update.StartsAtUtc);
        Assert.Equal(create.TotalCapacity, update.TotalCapacity);
        Assert.Equal(
            create.PricingTiers.Select(t => (t.Name, t.PriceAmount, t.PriceCurrency, t.Allocation)),
            update.PricingTiers.Select(t => (t.Name, t.PriceAmount, t.PriceCurrency, t.Allocation)));
    }

    [Fact]
    public void A_missing_description_maps_to_null_rather_than_an_empty_string()
    {
        _request.Description = null;

        Assert.Null(_request.ToCreateInput().Description);
    }

    [Fact]
    public void Mapping_copies_the_tiers_instead_of_sharing_the_request_list()
    {
        CreateEventInput input = _request.ToCreateInput();

        _request.PricingTiers.Clear();

        Assert.Equal(2, input.PricingTiers.Count);
    }

    [Fact]
    public void Purchase_mapping_takes_the_event_and_key_from_outside_the_body()
    {
        PurchaseTicketsInput input = _purchase.ToInput(EventId, "order-100");

        Assert.Equal(EventId, input.EventId);
        Assert.Equal("order-100", input.IdempotencyKey);
        Assert.Equal("buyer@example.com", input.PurchaserEmail);
        Assert.Equal([GaTier, VipTier], input.PurchaseItems.Select(i => i.PricingTierId));
        Assert.Equal([2, 1], input.PurchaseItems.Select(i => i.Quantity));
    }

    [Fact]
    public void An_empty_item_list_maps_to_an_empty_purchase_rather_than_throwing()
    {
        _purchase.PurchaseItems.Clear();

        PurchaseTicketsInput input = _purchase.ToInput(EventId, "order-100");

        // Mapping is not validation - the empty list has to survive the trip so
        // the layer that rejects it can see it.
        Assert.Empty(input.PurchaseItems);
    }
}
