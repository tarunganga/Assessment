using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Inputs;

namespace Ripple.Treasury.Assessment.UnitTests.Services;

public class EventServiceCapacityTests
{
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");

    private static List<PricingTierInput> Tiers(params int[] allocations)
    {
        List<PricingTierInput> tiers = [];
        int index = 0;

        foreach (int allocation in allocations)
        {
            index++;
            tiers.Add(new PricingTierInput
            {
                Name = $"Tier {index}",
                PriceAmount = 10m,
                PriceCurrency = "USD",
                Allocation = allocation
            });
        }

        return tiers;
    }

    [Fact]
    public void Allocations_summing_to_capacity_are_accepted()
    {
        EventService.ValidateEventCapacity(EventId, Tiers(60, 40), 100);
    }

    [Fact]
    public void Allocations_under_capacity_are_rejected()
    {
        CapacityViolationException error = Assert.Throws<CapacityViolationException>(
            () => EventService.ValidateEventCapacity(EventId, Tiers(60, 30), 100));

        Assert.Contains("90", error.Message);
        Assert.Contains("100", error.Message);
        Assert.Equal(EventId, error.EventId);
    }

    [Fact]
    public void Allocations_over_capacity_are_rejected()
    {
        Assert.Throws<CapacityViolationException>(
            () => EventService.ValidateEventCapacity(EventId, Tiers(60, 50), 100));
    }

    [Fact]
    public void No_tiers_cannot_satisfy_a_positive_capacity()
    {
        Assert.Throws<CapacityViolationException>(
            () => EventService.ValidateEventCapacity(EventId, Tiers(), 1));
    }

    [Fact]
    public void A_single_tier_may_hold_the_whole_capacity()
    {
        EventService.ValidateEventCapacity(EventId, Tiers(500_000), 500_000);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, 5)]
    [InlineData(10, 10)]   // boundary: selling every seat still allows the allocation
    public void Allocation_at_or_above_sold_is_accepted(int allocation, int sold)
    {
        EventService.RequireAllocationCoversSold(EventId, "GA", allocation, sold);
    }

    [Theory]
    [InlineData(9, 10)]
    [InlineData(0, 1)]
    public void Allocation_below_sold_is_rejected(int allocation, int sold)
    {
        CapacityViolationException error = Assert.Throws<CapacityViolationException>(
            () => EventService.RequireAllocationCoversSold(EventId, "GA", allocation, sold));

        Assert.Contains("GA", error.Message);
        Assert.Contains(sold.ToString(), error.Message);
    }

    [Fact]
    public void An_unsold_tier_may_be_removed()
    {
        EventService.RequireTierIsUnsoldBeforeRemoval(EventId, "VIP", 0);
    }

    [Fact]
    public void A_tier_with_sales_may_not_be_removed()
    {
        CapacityViolationException error = Assert.Throws<CapacityViolationException>(
            () => EventService.RequireTierIsUnsoldBeforeRemoval(EventId, "VIP", 1));

        Assert.Contains("VIP", error.Message);
        Assert.Contains("cannot be removed", error.Message);
    }

    [Fact]
    public void Tiers_sharing_one_currency_are_accepted()
    {
        EventService.ValidateSingleCurrency(EventId, Tiers(60, 40));
    }

    [Fact]
    public void An_event_with_no_tiers_has_no_currency_to_disagree_on()
    {
        EventService.ValidateSingleCurrency(EventId, Tiers());
    }

    [Fact]
    public void Tiers_in_different_currencies_are_rejected()
    {
        List<PricingTierInput> tiers = Tiers(60, 40);
        tiers[1].PriceCurrency = "EUR";

        CapacityViolationException error = Assert.Throws<CapacityViolationException>(
            () => EventService.ValidateSingleCurrency(EventId, tiers));

        Assert.Contains("USD", error.Message);
        Assert.Contains("EUR", error.Message);
        Assert.Equal(EventId, error.EventId);
    }

    [Fact]
    public void The_mismatch_is_caught_wherever_it_appears()
    {
        List<PricingTierInput> tiers = Tiers(50, 30, 20);
        tiers[2].PriceCurrency = "GBP";

        Assert.Throws<CapacityViolationException>(
            () => EventService.ValidateSingleCurrency(EventId, tiers));
    }

    [Fact]
    public void Total_allocation_sums_every_tier()
    {
        Assert.Equal(150, EventService.TotalAllocation(Tiers(60, 40, 50)));
        Assert.Equal(0, EventService.TotalAllocation(Tiers()));
    }
}
