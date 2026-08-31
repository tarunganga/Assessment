using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class EventServiceTests(PostgresFixture fixture) : IAsyncLifetime
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
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = ga },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = vip }
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
        await using TicketingDbContext db = fixture.CreateDbContext();

        Guid eventId = await NewService(db).CreateAsync(NewEvent(), default);

        await using TicketingDbContext check = fixture.CreateDbContext();

        Assert.Equal(100, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(2, await check.PricingTiers.CountAsync(t => t.EventId == eventId));
        Assert.Equal(EventStatus.Draft, (await check.Events.SingleAsync(e => e.Id == eventId)).Status);

        // one row per allocated seat in the tier, no id reused
        List<Guid> ga = await check.Tickets
            .Where(t => t.EventId == eventId && t.PricingTier!.Name == "GA")
            .Select(t => t.Id)
            .ToListAsync();

        Assert.Equal(60, ga.Count);
        Assert.Equal(60, ga.Distinct().Count());
        Assert.All(await check.Tickets.Where(t => t.EventId == eventId).ToListAsync(),
            t => Assert.Equal(TicketStatus.Available, t.Status));
    }

    [Fact]
    public async Task Create_rejects_allocations_that_do_not_sum_to_capacity()
    {
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
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 0 },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
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
                new PricingTierInput { Name = "GA", PriceAmount = 55m, PriceCurrency = "USD", Allocation = 90 },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 20 }
            ]
        };

        await service.UpdateAsync(eventId, resize, default);

        await using TicketingDbContext check = fixture.CreateDbContext();
        Assert.Equal(110, await check.Tickets.CountAsync(t => t.EventId == eventId));
        Assert.Equal(90, await check.Tickets.CountAsync(t => t.PricingTier!.Name == "GA"));
        Assert.Equal(20, await check.Tickets.CountAsync(t => t.PricingTier!.Name == "VIP"));
        Assert.Equal("Stadium", (await check.Events.SingleAsync(e => e.Id == eventId)).Venue);
        Assert.Equal(55m, (await check.PricingTiers.SingleAsync(t => t.Name == "GA")).PriceAmount);

        // no duplicate seats introduced by the grow
        List<Guid> seats = await check.Tickets
            .Where(t => t.PricingTier!.Name == "GA")
            .Select(t => t.Id)
            .ToListAsync();
        Assert.Equal(90, seats.Distinct().Count());
    }

    [Fact]
    public async Task Shrink_releases_the_seats_a_grow_added_before_the_original_ones()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        EventService service = NewService(db);

        Guid eventId = await service.CreateAsync(NewEvent(), default);

        List<Guid> original;

        await using (TicketingDbContext before = fixture.CreateDbContext())
        {
            original = await before.Tickets
                .Where(t => t.PricingTier!.Name == "GA")
                .Select(t => t.Id)
                .OrderBy(id => id)
                .ToListAsync();
        }

        await service.UpdateAsync(eventId, Resize(90, 20), default);
        await service.UpdateAsync(eventId, Resize(60, 40), default);

        // The sale takes the lowest ids, so a shrink has to give back the highest.
        // That only holds if PostgreSQL orders uuid v7 the way it was generated -
        // this is the test that pins it.
        await using TicketingDbContext check = fixture.CreateDbContext();
        List<Guid> remaining = await check.Tickets
            .Where(t => t.PricingTier!.Name == "GA")
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(60, remaining.Count);
        Assert.Equal(original, remaining);
    }

    private static UpdateEventInput Resize(int ga, int vip)
    {
        return new UpdateEventInput
        {
            Name = "Test Show",
            Venue = "Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = ga + vip,
            PricingTiers =
            [
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = ga },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = vip }
            ]
        };
    }

    [Fact]
    public async Task Update_on_a_cancelled_event_is_rejected()
    {
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
                    new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 60 },
                    new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
                ]
            }, default));
    }

    [Fact]
    public async Task Missing_event_throws_not_found()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<EventNotFoundException>(
            () => NewService(db).PublishAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Concurrent_updates_are_serialised_by_the_event_row_lock()
    {
        Guid eventId;
        await using (TicketingDbContext seed = fixture.CreateDbContext())
        {
            eventId = await NewService(seed).CreateAsync(NewEvent(), default);
        }

        // Both callers grow GA from 60 to 90. Unserialised they each read the same
        // total of 60 and each seed 30 more, leaving 120 seats in a tier
        // allocated 90.
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

        List<Guid> seats = await check.Tickets
            .Where(t => t.PricingTier!.Name == "GA")
            .Select(t => t.Id)
            .ToListAsync();
        Assert.Equal(90, seats.Distinct().Count());
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
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 90 },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 20 }
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
