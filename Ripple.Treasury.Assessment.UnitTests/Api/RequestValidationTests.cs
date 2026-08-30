using System.ComponentModel.DataAnnotations;
using Ripple.Treasury.Assessment.Api.Models.Requests;

namespace Ripple.Treasury.Assessment.UnitTests.Api;

public class RequestValidationTests
{
    // Validator does not recurse into list items the way the MVC model binder does,
    // so nested tiers and items are validated on their own below.
    private static List<ValidationResult> Validate(object model)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(model, new ValidationContext(model), results, true);
        return results;
    }

    private static SavePricingTierRequest Tier(string name, int allocation, decimal price = 50m)
    {
        return new SavePricingTierRequest
        {
            Name = name,
            PriceAmount = price,
            PriceCurrency = "USD",
            Allocation = allocation
        };
    }

    private static SaveEventRequest Event(params SavePricingTierRequest[] tiers)
    {
        int capacity = 0;

        foreach (SavePricingTierRequest tier in tiers)
        {
            capacity += tier.Allocation;
        }

        return new SaveEventRequest
        {
            Name = "Opening Night",
            Venue = "Royal Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = capacity,
            PricingTiers = [.. tiers]
        };
    }

    [Fact]
    public void A_well_formed_event_passes()
    {
        Assert.Empty(Validate(Event(Tier("GA", 60), Tier("VIP", 40))));
    }

    [Fact]
    public void Allocations_that_miss_the_capacity_are_reported_against_both_fields()
    {
        SaveEventRequest request = Event(Tier("GA", 60), Tier("VIP", 40));
        request.TotalCapacity = 150;

        ValidationResult error = Assert.Single(Validate(request));

        Assert.Contains("100", error.ErrorMessage);
        Assert.Contains("150", error.ErrorMessage);
        Assert.Equal(
            [nameof(SaveEventRequest.PricingTiers), nameof(SaveEventRequest.TotalCapacity)],
            error.MemberNames);
    }

    [Fact]
    public void Tier_names_are_unique_case_insensitively()
    {
        SaveEventRequest request = Event(Tier("VIP", 50), Tier("vip", 50));

        ValidationResult error = Assert.Single(Validate(request));
        Assert.Contains("unique", error.ErrorMessage);
    }

    [Fact]
    public void The_duplicate_name_rule_is_reported_once_however_many_repeats()
    {
        SaveEventRequest request = Event(Tier("GA", 30), Tier("GA", 30), Tier("GA", 40));

        Assert.Single(Validate(request));
    }

    [Fact]
    public void An_event_must_start_in_the_future()
    {
        SaveEventRequest request = Event(Tier("GA", 100));
        request.StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        ValidationResult error = Assert.Single(Validate(request));
        Assert.Contains("future", error.ErrorMessage);
        Assert.Equal([nameof(SaveEventRequest.StartsAtUtc)], error.MemberNames);
    }

    [Fact]
    public void Every_broken_rule_is_reported_together()
    {
        SaveEventRequest request = Event(Tier("GA", 60), Tier("ga", 40));
        request.TotalCapacity = 150;
        request.StartsAtUtc = DateTimeOffset.UtcNow.AddDays(-1);

        // One round trip should tell the caller everything that is wrong.
        Assert.Equal(3, Validate(request).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_event_needs_a_name(string name)
    {
        SaveEventRequest request = Event(Tier("GA", 100));
        request.Name = name;

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(nameof(SaveEventRequest.Name)));
    }

    [Fact]
    public void An_event_needs_at_least_one_tier()
    {
        SaveEventRequest request = Event();
        request.TotalCapacity = 1;

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(nameof(SaveEventRequest.PricingTiers)));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void A_three_letter_upper_case_currency_is_accepted(string currency)
    {
        SavePricingTierRequest tier = Tier("GA", 100);
        tier.PriceCurrency = currency;

        Assert.Empty(Validate(tier));
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("")]
    public void Anything_but_an_iso_code_is_rejected(string currency)
    {
        SavePricingTierRequest tier = Tier("GA", 100);
        tier.PriceCurrency = currency;

        Assert.Contains(Validate(tier),
            r => r.MemberNames.Contains(nameof(SavePricingTierRequest.PriceCurrency)));
    }

    [Fact]
    public void A_free_tier_is_allowed_but_a_negative_price_is_not()
    {
        Assert.Empty(Validate(Tier("GA", 100, 0m)));
        Assert.NotEmpty(Validate(Tier("GA", 100, -1m)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_tier_must_allocate_at_least_one_seat(int allocation)
    {
        Assert.Contains(Validate(Tier("GA", allocation)),
            r => r.MemberNames.Contains(nameof(SavePricingTierRequest.Allocation)));
    }

    [Fact]
    public void A_well_formed_purchase_passes()
    {
        Assert.Empty(Validate(Purchase((Guid.CreateVersion7(), 2), (Guid.CreateVersion7(), 1))));
    }

    [Fact]
    public void A_tier_may_appear_only_once_in_a_purchase()
    {
        Guid tier = Guid.CreateVersion7();

        ValidationResult error = Assert.Single(Validate(Purchase((tier, 1), (tier, 3))));
        Assert.Contains("only once", error.ErrorMessage);
    }

    [Fact]
    public void A_purchase_needs_at_least_one_item()
    {
        PurchaseTicketsRequest request = Purchase();

        Assert.Contains(Validate(request),
            r => r.MemberNames.Contains(nameof(PurchaseTicketsRequest.PurchaseItems)));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public void A_purchase_needs_a_real_looking_email(string email)
    {
        PurchaseTicketsRequest request = Purchase((Guid.CreateVersion7(), 1));
        request.PurchaserEmail = email;

        Assert.Contains(Validate(request),
            r => r.MemberNames.Contains(nameof(PurchaseTicketsRequest.PurchaserEmail)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void A_line_quantity_outside_one_to_fifty_is_rejected(int quantity)
    {
        PurchaseItemRequest item = new() { PricingTierId = Guid.CreateVersion7(), Quantity = quantity };

        Assert.Contains(Validate(item),
            r => r.MemberNames.Contains(nameof(PurchaseItemRequest.Quantity)));
    }

    private static PurchaseTicketsRequest Purchase(params (Guid Tier, int Qty)[] items)
    {
        PurchaseTicketsRequest request = new()
        {
            PurchaserEmail = "buyer@example.com"
        };

        foreach ((Guid tier, int qty) in items)
        {
            request.PurchaseItems.Add(new PurchaseItemRequest { PricingTierId = tier, Quantity = qty });
        }

        return request;
    }
}
