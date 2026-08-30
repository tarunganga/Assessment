using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Inputs;

namespace Ripple.Treasury.Assessment.UnitTests.Services;

public class TicketPurchaseServicePricingTests
{
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid GaId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid VipId = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    private static PricingTier Tier(Guid id, string name, decimal price, string currency = "USD")
    {
        return new PricingTier
        {
            Id = id, EventId = EventId, Name = name,
            PriceAmount = price, PriceCurrency = currency, Allocation = 100
        };
    }

    private static PurchaseItemInput Line(Guid tierId, int quantity)
    {
        return new PurchaseItemInput { PricingTierId = tierId, Quantity = quantity };
    }

    private static Purchase NewPurchase()
    {
        return new Purchase { Id = Guid.CreateVersion7(), EventId = EventId };
    }

    [Fact]
    public void Total_is_the_sum_of_every_line()
    {
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId,
            [Tier(GaId, "GA", 50.00m), Tier(VipId, "VIP", 150.00m)],
            [Line(GaId, 2), Line(VipId, 1)]);

        Assert.Equal(250.00m, purchase.TotalAmount);   // 2 x 50 + 1 x 150
        Assert.Equal("USD", purchase.Currency);
        Assert.Equal(2, purchase.Items.Count);
    }

    [Fact]
    public void Every_line_satisfies_item_total_equals_unit_price_times_quantity()
    {
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId,
            [Tier(GaId, "GA", 12.3400m), Tier(VipId, "VIP", 99.9900m)],
            [Line(GaId, 7), Line(VipId, 3)]);

        // This is exactly what ck_purchase_items_total_consistent enforces.
        Assert.All(purchase.Items, i => Assert.Equal(i.UnitPrice * i.Quantity, i.ItemTotal));
        Assert.Equal(purchase.Items.Sum(i => i.ItemTotal), purchase.TotalAmount);
    }

    [Fact]
    public void Unit_price_is_frozen_from_the_tier_at_pricing_time()
    {
        PricingTier ga = Tier(GaId, "GA", 50.00m);
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId, [ga], [Line(GaId, 2)]);

        ga.PriceAmount = 999.00m;   // a later admin repricing

        Assert.Equal(50.00m, purchase.Items[0].UnitPrice);
        Assert.Equal(100.00m, purchase.TotalAmount);
    }

    [Fact]
    public void Fractional_prices_do_not_drift()
    {
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId, [Tier(GaId, "GA", 33.3333m)], [Line(GaId, 3)]);

        // decimal, not double: 99.9999 exactly, and it fits numeric(19,4).
        Assert.Equal(99.9999m, purchase.TotalAmount);
        Assert.Equal(4, decimal.GetBits(purchase.TotalAmount)[3] >> 16 & 0xFF);
    }

    [Fact]
    public void The_smallest_representable_price_scales_exactly()
    {
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId, [Tier(GaId, "GA", 0.0001m)], [Line(GaId, 10_000)]);

        Assert.Equal(1.0000m, purchase.TotalAmount);
    }

    [Fact]
    public void A_free_tier_prices_to_zero()
    {
        Purchase purchase = NewPurchase();

        TicketPurchaseService.ApplyPricing(purchase, EventId, [Tier(GaId, "GA", 0m)], [Line(GaId, 5)]);

        Assert.Equal(0m, purchase.TotalAmount);
        Assert.Equal(0m, purchase.Items[0].ItemTotal);
    }

    [Fact]
    public void A_tier_that_does_not_belong_to_the_event_is_rejected()
    {
        PricingTierNotFoundException error = Assert.Throws<PricingTierNotFoundException>(
            () => TicketPurchaseService.ApplyPricing(NewPurchase(), EventId, [Tier(GaId, "GA", 50m)], [Line(VipId, 1)]));

        Assert.Equal(VipId, error.PricingTierId);
        Assert.Equal(EventId, error.EventId);
    }

    [Fact]
    public void Mixing_currencies_in_one_purchase_is_rejected()
    {
        CapacityViolationException error = Assert.Throws<CapacityViolationException>(
            () => TicketPurchaseService.ApplyPricing(NewPurchase(), EventId,
                [Tier(GaId, "GA", 50m, "USD"), Tier(VipId, "VIP", 150m, "EUR")],
                [Line(GaId, 1), Line(VipId, 1)]));

        Assert.Contains("USD", error.Message);
        Assert.Contains("EUR", error.Message);
    }



}
