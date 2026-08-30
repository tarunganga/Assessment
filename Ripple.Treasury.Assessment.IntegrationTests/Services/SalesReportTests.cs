using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class SalesReportTests(PostgresFixture fixture) : IAsyncLifetime
{
    // xUnit builds the class once per test, so every test starts on an empty schema.
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private async Task<(Guid EventId, Guid Ga, Guid Vip)> PublishedEventAsync()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService events = new(db);

        Guid eventId = await events.CreateAsync(new CreateEventInput
        {
            Name = "Concert",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = 15,
            PricingTiers =
            [
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 10 },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 5 }
            ]
        }, default);

        await events.PublishAsync(eventId, default);

        await using TicketingDbContext read = fixture.CreateDbContext();
        Guid gaId = (await read.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == "GA")).Id;
        Guid vipId = (await read.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == "VIP")).Id;
        return (eventId, gaId, vipId);
    }

    private async Task<Guid> BuyAsync(Guid eventId, string key, params (Guid Tier, int Qty)[] items)
    {
        PurchaseTicketsInput input = new()
        {
            EventId = eventId,
            IdempotencyKey = key,
            PurchaserEmail = "buyer@example.com"
        };

        foreach ((Guid tier, int qty) in items)
        {
            input.PurchaseItems.Add(new PurchaseItemInput { PricingTierId = tier, Quantity = qty });
        }

        await using TicketingDbContext db = fixture.CreateDbContext();
        PurchaseResult result = await new TicketPurchaseService(db).PurchaseAsync(input, default);
        return result.PurchaseId;
    }

    private async Task CancelAsync(Guid purchaseId)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        Purchase purchase = await db.Purchases.SingleAsync(p => p.Id == purchaseId);
        purchase.Status = PurchaseStatus.Cancelled;
        await db.SaveChangesAsync();
    }

    private async Task<SalesReport> ReportAsync(Guid eventId)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        return await new EventService(db).GetSalesReportAsync(eventId, default);
    }

    [Fact]
    public async Task The_report_totals_every_completed_purchase()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));

        SalesReport report = await ReportAsync(eventId);

        Assert.Equal(250m, report.TotalRevenue);
        Assert.Equal("USD", report.Currency);
        Assert.Equal(1, report.PurchaseCount);
        Assert.Equal(3, report.TicketsSold);
        Assert.Equal(12, report.TicketsAvailable);
    }

    [Fact]
    public async Task Tier_revenue_adds_up_to_the_event_total()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));
        await BuyAsync(eventId, "key-2", (ga, 3));

        SalesReport report = await ReportAsync(eventId);

        Assert.Equal(report.TotalRevenue, report.PricingTiers.Sum(t => t.Revenue));
        Assert.Equal(250m, report.PricingTiers.Single(t => t.Name == "GA").Revenue);
        Assert.Equal(150m, report.PricingTiers.Single(t => t.Name == "VIP").Revenue);
    }

    [Fact]
    public async Task A_cancelled_purchase_leaves_the_tier_breakdown_and_the_total_in_step()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));
        Guid cancelled = await BuyAsync(eventId, "key-2", (ga, 3));
        await CancelAsync(cancelled);

        SalesReport report = await ReportAsync(eventId);

        // The cancelled purchase must drop out of both numbers, not just the total.
        Assert.Equal(250m, report.TotalRevenue);
        Assert.Equal(1, report.PurchaseCount);
        Assert.Equal(report.TotalRevenue, report.PricingTiers.Sum(t => t.Revenue));
        Assert.Equal(100m, report.PricingTiers.Single(t => t.Name == "GA").Revenue);
    }

    [Fact]
    public async Task An_event_with_no_sales_reports_zero_rather_than_null()
    {
        (Guid eventId, Guid _, Guid _) = await PublishedEventAsync();

        SalesReport report = await ReportAsync(eventId);

        Assert.Equal(0m, report.TotalRevenue);
        Assert.Equal(0, report.PurchaseCount);
        Assert.Equal("USD", report.Currency);
        Assert.All(report.PricingTiers, tier => Assert.Equal(0m, tier.Revenue));
    }

    [Fact]
    public async Task A_mixed_currency_event_cannot_be_created()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        CapacityViolationException error = await Assert.ThrowsAsync<CapacityViolationException>(
            () => new EventService(db).CreateAsync(new CreateEventInput
            {
                Name = "Concert",
                Venue = "Arena",
                StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                TotalCapacity = 15,
                PricingTiers =
                [
                    new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 10 },
                    new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "EUR", Allocation = 5 }
                ]
            }, default));

        Assert.Contains("USD", error.Message);
        Assert.Contains("EUR", error.Message);
    }
}
