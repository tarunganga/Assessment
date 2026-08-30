using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

// A purchase and an event cancellation both have to agree on the event's status.
// The purchase takes FOR SHARE on the event row; cancelling takes FOR UPDATE.
// These conflict, so the two cannot interleave between the status check and the sale.
[Collection(IntegrationCollection.Name)]
public class EventCancellationRaceTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static readonly TimeSpan BlockedFor = TimeSpan.FromSeconds(1);

    private async Task<(Guid EventId, Guid Ga)> PublishedEventAsync()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService events = new(db);

        Guid eventId = await events.CreateAsync(new CreateEventInput
        {
            Name = "Concert",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = 10,
            PricingTiers =
            [
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 10 }
            ]
        }, default);

        await events.PublishAsync(eventId, default);

        await using TicketingDbContext read = fixture.CreateDbContext();
        Guid gaId = (await read.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == "GA")).Id;
        return (eventId, gaId);
    }

    private Task<PurchaseResult> BuyAsync(Guid eventId, Guid tierId, string key)
    {
        return Task.Run(async () =>
        {
            await using TicketingDbContext db = fixture.CreateDbContext();
            return await new TicketPurchaseService(db).PurchaseAsync(new PurchaseTicketsInput
            {
                EventId = eventId,
                IdempotencyKey = key,
                PurchaserEmail = "buyer@example.com",
                PurchaseItems = [new PurchaseItemInput { PricingTierId = tierId, Quantity = 1 }]
            }, default);
        });
    }

    [Fact]
    public async Task A_purchase_waits_for_an_in_flight_cancellation_and_then_sees_it()
    {
        (Guid eventId, Guid ga) = await PublishedEventAsync();

        // An earlier sale means the admin's delete soft-cancels rather than
        // hard-deleting, so the racing buyer has a row left to read.
        await BuyAsync(eventId, ga, "earlier-sale");

        // Stand in for the admin mid-DeleteAsync: hold the same FOR UPDATE that
        // LockEventAsync takes, flip the status, but do not commit yet.
        await using TicketingDbContext admin = fixture.CreateDbContext();
        await using IDbContextTransaction adminTransaction =
            await admin.Database.BeginTransactionAsync();

        await admin.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM events WHERE id = {eventId} FOR UPDATE");
        await admin.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE events SET status = 'Cancelled' WHERE id = {eventId}");

        Task<PurchaseResult> buyer = BuyAsync(eventId, ga, "racing-buyer");

        // Without the FOR SHARE in the purchase path this completes immediately,
        // selling a ticket against an event that is being cancelled.
        Task finished = await Task.WhenAny(buyer, Task.Delay(BlockedFor));
        Assert.NotSame(buyer, finished);

        await adminTransaction.CommitAsync();

        // Unblocked, the purchase re-reads the row and finds the cancellation.
        InvalidEventStateException error =
            await Assert.ThrowsAsync<InvalidEventStateException>(() => buyer);
        Assert.Equal(eventId, error.EventId);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(1, await check.Purchases.CountAsync(p => p.EventId == eventId));
        Assert.Equal(1, await check.Tickets.CountAsync(
            t => t.EventId == eventId && t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task A_cancellation_waits_for_an_in_flight_purchase()
    {
        (Guid eventId, Guid ga) = await PublishedEventAsync();

        // Stand in for a buyer mid-PurchaseAsync, holding the FOR SHARE.
        await using TicketingDbContext buyer = fixture.CreateDbContext();
        await using IDbContextTransaction buyerTransaction =
            await buyer.Database.BeginTransactionAsync();

        await buyer.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM events WHERE id = {eventId} FOR SHARE");

        Task admin = Task.Run(async () =>
        {
            await using TicketingDbContext db = fixture.CreateDbContext();
            await new EventService(db).DeleteAsync(eventId, default);
        });

        Task finished = await Task.WhenAny(admin, Task.Delay(BlockedFor));
        Assert.NotSame(admin, finished);

        await buyerTransaction.CommitAsync();
        await admin;

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Null(await check.Events.FirstOrDefaultAsync(e => e.Id == eventId));
    }

    [Fact]
    public async Task Concurrent_buyers_do_not_block_each_other_on_the_event_row()
    {
        (Guid eventId, Guid ga) = await PublishedEventAsync();

        // FOR SHARE does not conflict with itself, so the event lock must not
        // serialise ordinary sales - only the oversell guard should.
        Task<PurchaseResult>[] buyers =
        [
            BuyAsync(eventId, ga, "buyer-1"),
            BuyAsync(eventId, ga, "buyer-2"),
            BuyAsync(eventId, ga, "buyer-3")
        ];

        PurchaseResult[] results = await Task.WhenAll(buyers);

        Assert.All(results, result => Assert.False(result.IsReplay));
        Assert.Equal(3, results.Select(r => r.PurchaseId).Distinct().Count());

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(3, await check.Tickets.CountAsync(
            t => t.EventId == eventId && t.Status == TicketStatus.Sold));
    }
}
