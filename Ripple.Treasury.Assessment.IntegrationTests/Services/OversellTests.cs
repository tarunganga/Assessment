using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services;
using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class OversellTests(PostgresFixture fixture) : IAsyncLifetime
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

    private const int Capacity = 100;
    private const int Buyers = 200;
    private const decimal Price = 50.0000m;

    [Fact]
    public async Task Two_hundred_concurrent_buyers_cannot_oversell_a_hundred_seats()
    {
        Guid eventId = Guid.CreateVersion7();
        Guid tierId = Guid.CreateVersion7();
        await SeedAsync(eventId, tierId);

        // Release every buyer at the same instant so they genuinely contend
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task<bool>> buyers = new(Buyers);

        for (int i = 0; i < Buyers; i++)
        {
            int buyer = i;
            buyers.Add(Task.Run(async () =>
            {
                await start.Task;
                return await BuyOneAsync(eventId, tierId, buyer);
            }));
        }

        start.SetResult();
        bool[] outcomes = await Task.WhenAll(buyers);

        int succeeded = outcomes.Count(x => x);
        int failed = outcomes.Length - succeeded;

        await using TicketingDbContext db = fixture.CreateDbContext();

        int sold = await db.Tickets.CountAsync(t => t.Status == TicketStatus.Sold);
        int available = await db.Tickets.CountAsync(t => t.Status == TicketStatus.Available);
        int orphaned = await db.Tickets.CountAsync(t => t.Status == TicketStatus.Sold && t.PurchaseId == null);
        int itemQuantity = await db.PurchaseItems.SumAsync(i => i.Quantity);
        int purchases = await db.Purchases.CountAsync();

        Assert.Equal(Capacity, succeeded);
        Assert.Equal(Buyers - Capacity, failed);

        Assert.Equal(Capacity, sold);
        Assert.Equal(0, available);

        // Reconciliation. A plain count of 100 could be reached with one orphaned
        // row and one missing one, so count alone is not enough.
        Assert.Equal(0, orphaned);
        Assert.Equal(sold, itemQuantity);
        Assert.Equal(Capacity, purchases);

        // Every sold ticket belongs to a purchase that actually exists.
        int soldWithMissingPurchase = await db.Tickets
            .Where(t => t.Status == TicketStatus.Sold)
            .CountAsync(t => !db.Purchases.Any(p => p.Id == t.PurchaseId));

        Assert.Equal(0, soldWithMissingPurchase);

        // No ticket claimed twice.
        int distinctSold = await db.Tickets
            .Where(t => t.Status == TicketStatus.Sold)
            .Select(t => t.Id)
            .Distinct()
            .CountAsync();

        Assert.Equal(sold, distinctSold);
    }

    private async Task<bool> BuyOneAsync(Guid eventId, Guid tierId, int buyer)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        try
        {
            await new TicketPurchaseService(db).PurchaseAsync(new PurchaseTicketsInput
            {
                EventId = eventId,
                IdempotencyKey = $"buyer-{buyer}",
                PurchaserEmail = $"buyer{buyer}@example.com",
                PurchaseItems = [new PurchaseItemInput { PricingTierId = tierId, Quantity = 1 }]
            }, default);

            return true;
        }
        catch (InsufficientInventoryException)
        {
            return false;
        }
    }

    private async Task SeedAsync(Guid eventId, Guid tierId)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        db.Events.Add(new Event
        {
            Id = eventId,
            Name = "Sold Out Show",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = Capacity,
            Status = EventStatus.Published
        });

        db.PricingTiers.Add(new PricingTier
        {
            Id = tierId,
            EventId = eventId,
            Name = "General Admission",
            PriceAmount = Price,
            PriceCurrency = "USD",
            Allocation = Capacity
        });

        for (int ordinal = 1; ordinal <= Capacity; ordinal++)
        {
            db.Tickets.Add(new Ticket
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                PricingTierId = tierId,
                SeatOrdinal = ordinal,
                Status = TicketStatus.Available
            });
        }

        await db.SaveChangesAsync();
    }
}
