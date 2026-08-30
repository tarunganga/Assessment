using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services;

namespace Ripple.Treasury.Assessment.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class EventServiceTests(PostgresFixture fixture)
{
    private static CreateEventInput NewEvent(int ga = 60, int vip = 40)
    {
        return new CreateEventInput
        {
            Name = "Test Show",
            Description = "A show",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = ga + vip,
            PricingTiers =
            [
                new() { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = ga },
                new() { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = vip }
            ]
        };
    }

    private EventService NewService(TicketingDbContext db)
    {
        return new EventService(db);
    }

    [Fact]
    public async Task Create_seeds_one_ticket_per_allocated_seat()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();

        Guid eventId = await NewService(db).CreateAsync(NewEvent(), default);

        await using TicketingDbContext check = fixture.CreateDbContext();

        Assert.Equal(100, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(2, await check.PricingTiers.CountAsync(t => t.EventId == eventId));
        Assert.Equal(EventStatus.Draft, (await check.Events.SingleAsync(e => e.Id == eventId)).Status);

        // ordinals are 1..allocation per tier, dense and unique
        List<int> ga = await check.Tickets
            .Where(t => t.EventId == eventId && t.PricingTier!.Name == "GA")
            .Select(t => t.SeatOrdinal)
            .OrderBy(o => o)
            .ToListAsync();

        Assert.Equal(60, ga.Count);
        Assert.Equal(1, ga[0]);
        Assert.Equal(60, ga[59]);
        Assert.Equal(60, ga.Distinct().Count());
        Assert.All(await check.Tickets.Where(t => t.EventId == eventId).ToListAsync(),
            t => Assert.Equal(TicketStatus.Available, t.Status));
    }

    [Fact]
    public async Task Create_rejects_allocations_that_do_not_sum_to_capacity()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();

        CreateEventInput input = NewEvent();
        input.TotalCapacity = 999;

        await Assert.ThrowsAsync<CapacityViolationException>(
            () => NewService(db).CreateAsync(input, default));

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(0, await check.Events.CountAsync());
        Assert.Equal(0, await check.Tickets.CountAsync());
    }

    [Fact]
    public async Task Publish_moves_draft_to_published_and_is_idempotent()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);
        await service.PublishAsync(eventId, default);
        await service.PublishAsync(eventId, default);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Published, (await check.Events.SingleAsync(e => e.Id == eventId)).Status);
    }

    [Fact]
    public async Task Delete_hard_deletes_when_nothing_is_sold()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);
        await service.DeleteAsync(eventId, default);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(0, await check.Events.CountAsync());
        Assert.Equal(0, await check.PricingTiers.CountAsync());
        Assert.Equal(0, await check.Tickets.CountAsync());
    }

    [Fact]
    public async Task Delete_cancels_instead_of_destroying_when_tickets_are_sold()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);
        await SellOneAsync(eventId, "GA");

        await service.DeleteAsync(eventId, default);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Cancelled, (await check.Events.SingleAsync(e => e.Id == eventId)).Status);
        Assert.Equal(100, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(1, await check.Tickets.CountAsync(t => t.Status == TicketStatus.Sold));
    }

    [Fact]
    public async Task Update_cannot_shrink_a_tier_below_what_is_already_sold()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);
        await SellOneAsync(eventId, "GA");

        UpdateEventInput shrink = new()
        {
            Name = "Test Show",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = 40,
            PricingTiers =
            [
                new() { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 0 },
                new() { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
            ]
        };

        await Assert.ThrowsAsync<CapacityViolationException>(
            () => service.UpdateAsync(eventId, shrink, default));

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(100, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(100, (await check.Events.SingleAsync(e => e.Id == eventId)).TotalCapacity);
    }

    [Fact]
    public async Task Update_grows_and_shrinks_inventory_to_match_allocation()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);

        UpdateEventInput resize = new()
        {
            Name = "Bigger Show",
            Venue = "Stadium",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(45),
            TotalCapacity = 110,
            PricingTiers =
            [
                new() { Name = "GA", PriceAmount = 55m, PriceCurrency = "USD", Allocation = 90 },
                new() { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 20 }
            ]
        };

        await service.UpdateAsync(eventId, resize, default);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(110, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(90, await check.Tickets.CountAsync(t => t.PricingTier!.Name == "GA"));
        Assert.Equal(20, await check.Tickets.CountAsync(t => t.PricingTier!.Name == "VIP"));
        Assert.Equal("Stadium", (await check.Events.SingleAsync(e => e.Id == eventId)).Venue);
        Assert.Equal(55m, (await check.PricingTiers.SingleAsync(t => t.Name == "GA")).PriceAmount);

        // no duplicate ordinals introduced by the grow
        List<int> ordinals = await check.Tickets
            .Where(t => t.PricingTier!.Name == "GA")
            .Select(t => t.SeatOrdinal)
            .ToListAsync();
        Assert.Equal(90, ordinals.Distinct().Count());
    }

    [Fact]
    public async Task Update_on_a_cancelled_event_is_rejected()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);
        await SellOneAsync(eventId, "GA");
        await service.DeleteAsync(eventId, default);

        await Assert.ThrowsAsync<InvalidEventStateException>(
            () => service.UpdateAsync(eventId, new UpdateEventInput
            {
                Name = "x",
                Venue = "y",
                StartsAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                TotalCapacity = 100,
                PricingTiers =
                [
                    new() { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 60 },
                    new() { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
                ]
            }, default));
    }

    [Fact]
    public async Task Missing_event_throws_not_found()
    {
        await fixture.ResetAsync();
        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<EventNotFoundException>(
            () => NewService(db).PublishAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Concurrent_updates_are_serialised_by_the_event_row_lock()
    {
        await fixture.ResetAsync();

        Guid eventId;
        await using (TicketingDbContext seed = fixture.CreateDbContext())
        {
            eventId = await NewService(seed).CreateAsync(NewEvent(), default);
        }

        // Both callers grow GA from 60 to 90. Unserialised they each read the same
        // highest ordinal and seed the same 30 ordinals, colliding on
        // uq_tickets_tier_ordinal - or silently produce 120 seats.
        TaskCompletionSource start = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> first = Task.Run(async () => await GrowAsync(eventId, start));
        Task<bool> second = Task.Run(async () => await GrowAsync(eventId, start));

        start.SetResult();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.Equal(2, outcomes.Count(x => x));

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(90, await check.Tickets.CountAsync(t => t.PricingTier!.Name == "GA"));
        Assert.Equal(110, await check.Tickets.CountAsync(t => t.EventId == eventId));

        List<int> ordinals = await check.Tickets
            .Where(t => t.PricingTier!.Name == "GA")
            .Select(t => t.SeatOrdinal)
            .ToListAsync();
        Assert.Equal(90, ordinals.Distinct().Count());
    }

    private async Task<bool> GrowAsync(Guid eventId, TaskCompletionSource start)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        await start.Task;

        await NewService(db).UpdateAsync(eventId, new UpdateEventInput
        {
            Name = "Test Show",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = 110,
            PricingTiers =
            [
                new() { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 90 },
                new() { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 20 }
            ]
        }, default);

        return true;
    }


    private async Task SellOneAsync(Guid eventId, string tierName)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        Guid tierId = (await db.PricingTiers.SingleAsync(t => t.EventId == eventId && t.Name == tierName)).Id;

        // Selling requires a published event. Publish is idempotent.
        await new EventService(db).PublishAsync(eventId, default);

        await new TicketPurchaseService(db).PurchaseAsync(new PurchaseTicketsInput
        {
            EventId = eventId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            PurchaserEmail = "buyer@example.com",
            PurchaseItems = [new PurchaseItemInput { PricingTierId = tierId, Quantity = 1 }]
        }, default);
    }
}
