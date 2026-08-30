using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.IntegrationTests.Fixtures;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.IntegrationTests.Services;

[Collection(IntegrationCollection.Name)]
public class EventQueryTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static readonly DateTimeOffset Base =
        new(2027, 1, 1, 20, 0, 0, TimeSpan.Zero);

    private EventService NewService(TicketingDbContext db)
    {
        return new EventService(db);
    }

    private async Task<Guid> SeedAsync(string name, string venue, DateTimeOffset startsAt, int ga = 6, int vip = 4)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        return await NewService(db).CreateAsync(new CreateEventInput
        {
            Name = name,
            Description = $"{name} at {venue}",
            Venue = venue,
            StartsAtUtc = startsAt,
            TotalCapacity = ga + vip,
            PricingTiers =
            [
                new PricingTierInput { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = ga },
                new PricingTierInput { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = vip }
            ]
        }, default);
    }

    private async Task SeedThreeAsync()
    {
        await SeedAsync("Opening Night", "Royal Arena", Base);
        await SeedAsync("Second Night", "City Hall", Base.AddDays(1));
        await SeedAsync("Closing Night", "Royal Arena", Base.AddDays(2));
    }

    private async Task<PagedResult<EventSummary>> ListAsync(
        DateTimeOffset? from = null, string? venue = null, int page = 1, int pageSize = 20)
    {
        await using TicketingDbContext db = fixture.CreateDbContext();
        return await NewService(db).ListAsync(from, venue, page, pageSize, default);
    }

    [Fact]
    public async Task List_returns_every_event_oldest_first()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync();

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(["Opening Night", "Second Night", "Closing Night"],
            result.Items.Select(e => e.Name));
        Assert.All(result.Items, e => Assert.Equal("Draft", e.Status));
    }

    [Fact]
    public async Task List_pages_without_dropping_or_repeating_an_event()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> first = await ListAsync(page: 1, pageSize: 2);
        PagedResult<EventSummary> second = await ListAsync(page: 2, pageSize: 2);

        // TotalCount is the size of the match, not of the page.
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);

        List<Guid> seen = [.. first.Items.Select(e => e.Id), .. second.Items.Select(e => e.Id)];
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_rather_than_an_error()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync(page: 9, pageSize: 20);

        Assert.Empty(result.Items);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(9, result.Page);
    }

    [Fact]
    public async Task The_from_filter_keeps_events_starting_on_the_boundary()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync(from: Base.AddDays(1));

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Second Night", "Closing Night"], result.Items.Select(e => e.Name));
    }

    [Fact]
    public async Task The_venue_filter_matches_a_case_insensitive_substring()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync(venue: "royal");

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, e => Assert.Equal("Royal Arena", e.Venue));
    }

    [Fact]
    public async Task The_two_filters_narrow_together()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync(from: Base.AddDays(1), venue: "Royal");

        Assert.Equal("Closing Night", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task A_venue_nobody_booked_returns_an_empty_page()
    {
        await SeedThreeAsync();

        PagedResult<EventSummary> result = await ListAsync(venue: "Nowhere");

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task Get_returns_the_event_with_its_tiers()
    {
        Guid eventId = await SeedAsync("Opening Night", "Royal Arena", Base);

        await using TicketingDbContext db = fixture.CreateDbContext();
        EventDetail detail = await NewService(db).GetAsync(eventId, default);

        Assert.Equal(eventId, detail.Id);
        Assert.Equal("Opening Night", detail.Name);
        Assert.Equal("Royal Arena", detail.Venue);
        Assert.Equal(10, detail.TotalCapacity);
        Assert.Equal("Draft", detail.Status);
        Assert.Equal(2, detail.PricingTiers.Count);
        Assert.Equal(150m, detail.PricingTiers.Single(t => t.Name == "VIP").PriceAmount);
    }

    [Fact]
    public async Task Getting_an_event_that_does_not_exist_throws_not_found()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<EventNotFoundException>(
            () => NewService(db).GetAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Availability_starts_at_the_full_allocation()
    {
        Guid eventId = await SeedAsync("Opening Night", "Royal Arena", Base);

        await using TicketingDbContext db = fixture.CreateDbContext();
        EventAvailability availability = await NewService(db).GetAvailabilityAsync(eventId, default);

        Assert.Equal(10, availability.TotalAvailable);
        Assert.Equal("Draft", availability.Status);
        Assert.All(availability.PricingTiers, t => Assert.Equal(0, t.Sold));
        Assert.Equal(6, availability.PricingTiers.Single(t => t.Name == "GA").Available);

        // Ordered most expensive first.
        Assert.Equal(["VIP", "GA"], availability.PricingTiers.Select(t => t.Name));
    }

    [Fact]
    public async Task Availability_is_counted_from_the_tickets_after_a_sale()
    {
        Guid eventId = await SeedAsync("Opening Night", "Royal Arena", Base);

        await using (TicketingDbContext seed = fixture.CreateDbContext())
        {
            await NewService(seed).PublishAsync(eventId, default);
            Guid gaId = (await seed.PricingTiers
                .SingleAsync(t => t.EventId == eventId && t.Name == "GA")).Id;

            await new TicketPurchaseService(seed).PurchaseAsync(new PurchaseTicketsInput
            {
                EventId = eventId,
                IdempotencyKey = "sale-1",
                PurchaserEmail = "buyer@example.com",
                PurchaseItems = [new PurchaseItemInput { PricingTierId = gaId, Quantity = 2 }]
            }, default);
        }

        await using TicketingDbContext db = fixture.CreateDbContext();
        EventAvailability availability = await NewService(db).GetAvailabilityAsync(eventId, default);

        TierAvailability ga = availability.PricingTiers.Single(t => t.Name == "GA");

        Assert.Equal("Published", availability.Status);
        Assert.Equal(8, availability.TotalAvailable);
        Assert.Equal(2, ga.Sold);
        Assert.Equal(4, ga.Available);

        // Sold and available always account for the whole allocation.
        Assert.All(availability.PricingTiers,
            t => Assert.Equal(t.Allocation, t.Sold + t.Available));
    }

    [Fact]
    public async Task Availability_for_an_event_that_does_not_exist_throws_not_found()
    {
        await using TicketingDbContext db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<EventNotFoundException>(
            () => NewService(db).GetAvailabilityAsync(Guid.CreateVersion7(), default));
    }
}
