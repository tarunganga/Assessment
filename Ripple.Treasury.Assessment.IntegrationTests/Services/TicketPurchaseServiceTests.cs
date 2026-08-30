using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class TicketPurchaseServiceTests(PostgresFixture fixture) : IAsyncLifetime
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

    private TicketPurchaseService NewService(TicketingDbContext db)
    {
        return new TicketPurchaseService(db);
    }

    private async Task<(Guid EventId, Guid Ga, Guid Vip)> PublishedEventAsync(int ga = 10, int vip = 5)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService events = new(db);

        Guid eventId = await events.CreateAsync(new CreateEventInput
        {
            Name = "Concert",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = ga + vip,
            PricingTiers =
            [
                new PricingTierInput { Name = "GA", PriceAmount = 50.00m, PriceCurrency = "USD", Allocation = ga },
                new PricingTierInput { Name = "VIP", PriceAmount = 150.00m, PriceCurrency = "USD", Allocation = vip }
            ]
        }, default);

        await events.PublishAsync(eventId, default);

        await using TicketingDbContext read = fixture.CreateDbContext();
        Guid gaId = (await read.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == "GA")).Id;
        Guid vipId = (await read.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == "VIP")).Id;
        return (eventId, gaId, vipId);
    }

    private static PurchaseTicketsInput Buy(Guid eventId, string key, params (Guid Tier, int Qty)[] items)
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

        return input;
    }

    [Fact]
    public async Task Purchase_prices_from_the_tier_and_claims_the_tickets()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync();

        await using TicketingDbContext db = fixture.CreateDbContext();
        PurchaseResult result = await NewService(db).PurchaseAsync(
            Buy(eventId, "key-1", (ga, 2), (vip, 1)), default);

        Assert.False(result.IsReplay);

        await using TicketingDbContext check = fixture.CreateDbContext();

        // Priced from the tier, and asserted against what was actually persisted.
        Infrastructure.Entities.Purchase persisted =
            await check.Purchases.SingleAsync(p => p.Id == result.PurchaseId);
        Assert.Equal(250.00m, persisted.TotalAmount);   // 2 x 50 + 1 x 150
        Assert.Equal("USD", persisted.Currency);

        Assert.Equal(3, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
        Assert.Equal(2, await check.PurchaseItems.CountAsync());
        Assert.Equal(3, await check.PurchaseItems.SumAsync(i => i.Quantity));
        Assert.All(await check.Tickets.Where(t => t.Status == TicketStatus.Sold).ToListAsync(),
            t => Assert.Equal(result.PurchaseId, t.PurchaseId));
    }

    [Fact]
    public async Task Repricing_a_tier_does_not_move_historical_revenue()
    {
        (Guid eventId, Guid ga, _) = await PublishedEventAsync();

        await using (TicketingDbContext db = fixture.CreateDbContext())
        {
            await NewService(db).PurchaseAsync(Buy(eventId, "key-1", (ga, 2)), default);
        }

        await using (TicketingDbContext reprice = fixture.CreateDbContext())
        {
            Infrastructure.Entities.PricingTier tier = await reprice.PricingTiers.SingleAsync(t => t.Id == ga);
            tier.PriceAmount = 999.00m;
            await reprice.SaveChangesAsync();
        }

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(50.00m, (await check.PurchaseItems.SingleAsync()).UnitPrice);
        Assert.Equal(100.00m, (await check.Purchases.SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task Same_key_and_same_body_replays_instead_of_buying_twice()
    {
        (Guid eventId, Guid ga, _) = await PublishedEventAsync();

        await using TicketingDbContext db = fixture.CreateDbContext();
        TicketPurchaseService service = NewService(db);

        PurchaseResult first = await service.PurchaseAsync(Buy(eventId, "key-1", (ga, 2)), default);
        PurchaseResult second = await service.PurchaseAsync(Buy(eventId, "key-1", (ga, 2)), default);

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.PurchaseId, second.PurchaseId);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(1, await check.Purchases.CountAsync());
        Assert.Equal(2, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_a_conflict()
    {
        (Guid eventId, Guid ga, _) = await PublishedEventAsync();

        await using TicketingDbContext db = fixture.CreateDbContext();
        TicketPurchaseService service = NewService(db);

        await service.PurchaseAsync(Buy(eventId, "key-1", (ga, 2)), default);

        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(
            () => service.PurchaseAsync(Buy(eventId, "key-1", (ga, 5)), default));

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(1, await check.Purchases.CountAsync());
        Assert.Equal(2, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task Twenty_concurrent_identical_requests_produce_one_purchase()
    {
        (Guid eventId, Guid ga, _) = await PublishedEventAsync();

        TaskCompletionSource start = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task<PurchaseResult>> callers = new(20);

        for (int i = 0; i < 20; i++)
        {
            callers.Add(Task.Run(async () =>
            {
                await using TicketingDbContext db = fixture.CreateDbContext();
                await start.Task;
                return await NewService(db).PurchaseAsync(Buy(eventId, "same-key", (ga, 2)), default);
            }));
        }

        start.SetResult();
        PurchaseResult[] results = await Task.WhenAll(callers);

        Assert.Equal(1, results.Count(r => !r.IsReplay));
        Assert.Equal(19, results.Count(r => r.IsReplay));
        Assert.Single(results.Select(r => r.PurchaseId).Distinct());

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(1, await check.Purchases.CountAsync());
        Assert.Equal(2, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task A_short_second_tier_rolls_the_whole_purchase_back()
    {
        (Guid eventId, Guid ga, Guid vip) = await PublishedEventAsync(ga: 10, vip: 2);

        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<InsufficientInventoryException>(
            () => NewService(db).PurchaseAsync(Buy(eventId, "key-1", (ga, 3), (vip, 5)), default));

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(0, await check.Purchases.CountAsync());
        Assert.Equal(0, await check.PurchaseItems.CountAsync());
        Assert.Equal(0, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task Buying_from_a_draft_event_is_rejected()
    {
        Guid eventId;
        Guid ga;

        await using (TicketingDbContext setup = fixture.CreateDbContext())
        {
            EventService events = new(setup);
            eventId = await events.CreateAsync(new CreateEventInput
            {
                Name = "Draft", Venue = "Arena",
                StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                TotalCapacity = 10,
                PricingTiers =
                [
                    new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 10 }
                ]
            }, default);

            ga = (await setup.PricingTiers.SingleAsync(t => t.EventId == eventId)).Id;
        }

        await using TicketingDbContext db = fixture.CreateDbContext();
        await Assert.ThrowsAsync<InvalidEventStateException>(
            () => NewService(db).PurchaseAsync(Buy(eventId, "key-1", (ga, 1)), default));
    }

    [Fact]
    public async Task A_tier_from_another_event_is_rejected()
    {
        (Guid first, _, _) = await PublishedEventAsync();
        (_, Guid otherGa, _) = await PublishedEventAsync();

        await using TicketingDbContext db = fixture.CreateDbContext();
        await Assert.ThrowsAsync<PricingTierNotFoundException>(
            () => NewService(db).PurchaseAsync(Buy(first, "key-1", (otherGa, 1)), default));
    }
}
