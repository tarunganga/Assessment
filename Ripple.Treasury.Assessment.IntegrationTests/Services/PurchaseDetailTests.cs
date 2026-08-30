using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class PurchaseDetailTests(PostgresFixture fixture) : IAsyncLifetime
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

    private async Task<PurchaseDetail> DetailAsync(Guid purchaseId)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        return await new TicketPurchaseService(db).GetAsync(purchaseId, default);
    }

    [Fact]
    public async Task The_detail_carries_the_whole_purchase()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        Guid purchaseId = await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));
        PurchaseDetail detail = await DetailAsync(purchaseId);

        Assert.Equal(purchaseId, detail.Id);
        Assert.Equal(eventId, detail.EventId);
        Assert.Equal("buyer@example.com", detail.PurchaserEmail);
        Assert.Equal(250m, detail.TotalAmount);
        Assert.Equal("USD", detail.Currency);
        Assert.Equal("Completed", detail.Status);
        Assert.NotEqual(default, detail.CreatedAt);
    }

    [Fact]
    public async Task Each_line_names_its_tier_and_its_frozen_unit_price()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        Guid purchaseId = await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));
        PurchaseDetail detail = await DetailAsync(purchaseId);

        Assert.Equal(2, detail.Items.Count);

        PurchaseItemDetail gaLine = detail.Items.Single(i => i.PricingTierId == ga);
        Assert.Equal("GA", gaLine.PricingTierName);
        Assert.Equal(2, gaLine.Quantity);
        Assert.Equal(50m, gaLine.UnitPrice);
        Assert.Equal(100m, gaLine.ItemTotal);

        // The lines have to account for the total the buyer was charged.
        Assert.Equal(detail.TotalAmount, detail.Items.Sum(i => i.ItemTotal));
    }

    [Fact]
    public async Task The_detail_lists_one_ticket_per_seat_bought()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        Guid purchaseId = await BuyAsync(eventId, "key-1", (ga, 2), (vip, 1));
        PurchaseDetail detail = await DetailAsync(purchaseId);

        Assert.Equal(3, detail.TicketIds.Count);
        Assert.Equal(3, detail.TicketIds.Distinct().Count());

        await using TicketingDbContext check = fixture.CreateDbContext();
        List<Guid> sold = await check.Tickets
            .Where(t => t.PurchaseId == purchaseId && t.Status == TicketStatus.Sold)
            .Select(t => t.Id)
            .ToListAsync();

        Assert.Equal(sold.Order(), detail.TicketIds.Order());
    }

    [Fact]
    public async Task A_replayed_purchase_reads_back_as_the_original()
    {
        (Guid eventId, Guid ga, Guid _) = await PublishedEventAsync();

        Guid first = await BuyAsync(eventId, "key-1", (ga, 2));
        Guid replay = await BuyAsync(eventId, "key-1", (ga, 2));

        Assert.Equal(first, replay);

        PurchaseDetail detail = await DetailAsync(replay);

        // The replay must not have bought a second pair of seats.
        Assert.Equal(2, detail.TicketIds.Count);
        Assert.Equal(100m, detail.TotalAmount);
    }

    [Fact]
    public async Task A_purchase_that_does_not_exist_throws_not_found()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<PurchaseNotFoundException>(
            () => new TicketPurchaseService(db).GetAsync(Guid.CreateVersion7(), default));
    }
}
