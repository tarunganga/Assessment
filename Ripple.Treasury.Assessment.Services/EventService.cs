using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.Services;

public class EventService(TicketingDbContext db) : IEventService
{
    // One statement, however many seats. EF AddRange on 50k entities takes seconds.
    private const string CreateTicketsSql =
        """
        INSERT INTO tickets (id, event_id, pricing_tier_id, seat_ordinal, status)
        SELECT seed.id, {0}, {1}, {2} + seed.ordinal, 'Available'
        FROM unnest({3}) WITH ORDINALITY AS seed(id, ordinal);
        """;

    public async Task<Guid> CreateAsync(CreateEventInput input, CancellationToken cancellationToken)
    {
        // Create a new event id
        Guid eventId = Guid.CreateVersion7();

        // Validate the event capacity
        ValidateEventCapacity(eventId, input.PricingTiers, input.TotalCapacity);

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        Event newEvent = new()
        {
            Id = eventId,
            Name = input.Name,
            Description = input.Description,
            Venue = input.Venue,
            StartsAtUtc = input.StartsAtUtc,
            TotalCapacity = input.TotalCapacity,
            Status = EventStatus.Draft
        };

        db.Events.Add(newEvent);

        List<PricingTier> tiers = [];

        foreach (PricingTierInput tier in input.PricingTiers)
        {
            PricingTier pricingTier = new()
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                Name = tier.Name,
                PriceAmount = tier.PriceAmount,
                PriceCurrency = tier.PriceCurrency,
                Allocation = tier.Allocation
            };

            db.PricingTiers.Add(pricingTier);
            tiers.Add(pricingTier);
        }

        // PricingTiers must exist before tickets reference them.
        await db.SaveChangesAsync(cancellationToken);

        // Loop through each pricing tier and create tickets
        foreach (PricingTier tier in tiers)
        {
            await CreateTicketsAsync(eventId, tier.Id, NewTicketIds(tier.Allocation), 0, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return eventId;
    }

    public async Task UpdateAsync(Guid eventId, UpdateEventInput input, CancellationToken cancellationToken)
    {
        // Validate the event capacity
        ValidateEventCapacity(eventId, input.PricingTiers, input.TotalCapacity);

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Select the existing event - by locking it for update
        Event existing = await LockEventAsync(eventId, cancellationToken);

        // Check if the event is canceled
        if (existing.Status == EventStatus.Cancelled)
        {
            throw new InvalidEventStateException(eventId, existing.Status.ToString(), "updating");
        }

        List<PricingTier> currentTiers = await db.PricingTiers
            .Where(t => t.EventId == eventId)
            .ToListAsync(cancellationToken);

        foreach (PricingTierInput tier in input.PricingTiers)
        {
            PricingTier? current = FindByName(currentTiers, tier.Name);

            if (current == null)
            {
                PricingTier added = new()
                {
                    Id = Guid.CreateVersion7(),
                    EventId = eventId,
                    Name = tier.Name,
                    PriceAmount = tier.PriceAmount,
                    PriceCurrency = tier.PriceCurrency,
                    Allocation = tier.Allocation
                };

                db.PricingTiers.Add(added);
                await db.SaveChangesAsync(cancellationToken);
                await CreateTicketsAsync(eventId, added.Id, NewTicketIds(tier.Allocation), 0, cancellationToken);
                continue;
            }

            await ResizeTierAsync(eventId, current, tier, cancellationToken);

            current.PriceAmount = tier.PriceAmount;
            current.PriceCurrency = tier.PriceCurrency;
            current.Allocation = tier.Allocation;
        }

        foreach (PricingTier current in currentTiers)
        {
            if (FindByName(input.PricingTiers, current.Name) != null)
            {
                continue;
            }

            int sold = await SoldCountAsync(current.Id, cancellationToken);

            RequireTierIsUnsoldBeforeRemoval(eventId, current.Name, sold);

            db.PricingTiers.Remove(current);
        }

        existing.Name = input.Name;
        existing.Description = input.Description;
        existing.Venue = input.Venue;
        existing.StartsAtUtc = input.StartsAtUtc;
        existing.TotalCapacity = input.TotalCapacity;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PublishAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        Event existing = await LockEventAsync(eventId, cancellationToken);

        if (existing.Status == EventStatus.Cancelled)
        {
            throw new InvalidEventStateException(eventId, existing.Status.ToString(), "publishing");
        }

        if (existing.Status == EventStatus.Published)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        existing.Status = EventStatus.Published;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        Event existing = await LockEventAsync(eventId, cancellationToken);

        bool hasSales = await db.Tickets
            .AnyAsync(t => t.EventId == eventId && t.Status == TicketStatus.Sold, cancellationToken);

        if (hasSales)
        {
            // A financial record is never destroyed because an admin clicked delete.
            existing.Status = EventStatus.Cancelled;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.Events.Remove(existing);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Reads project straight into result types - no entities loaded.
    public async Task<EventDetail> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        EventDetail? detail = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new EventDetail
            {
                Id = e.Id,
                Name = e.Name,
                Description = e.Description,
                Venue = e.Venue,
                StartsAtUtc = e.StartsAtUtc,
                TotalCapacity = e.TotalCapacity,
                Status = e.Status.ToString(),
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
                PricingTiers = e.Tiers
                    .OrderByDescending(t => t.PriceAmount)
                    .Select(t => new PricingTierDetail
                    {
                        Id = t.Id,
                        Name = t.Name,
                        PriceAmount = t.PriceAmount,
                        PriceCurrency = t.PriceCurrency,
                        Allocation = t.Allocation
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return detail ?? throw new EventNotFoundException(eventId);
    }

    public async Task<PagedResult<EventSummary>> ListAsync(
        DateTimeOffset? from, string? venue, int page, int pageSize, CancellationToken cancellationToken)
    {
        IQueryable<Event> query = db.Events;

        if (from.HasValue)
        {
            query = query.Where(e => e.StartsAtUtc >= from.Value);
        }

        if (!string.IsNullOrWhiteSpace(venue))
        {
            query = query.Where(e => EF.Functions.ILike(e.Venue, $"%{venue}%"));
        }

        int total = await query.CountAsync(cancellationToken);

        List<EventSummary> items = await query
            .OrderBy(e => e.StartsAtUtc)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EventSummary
            {
                Id = e.Id,
                Name = e.Name,
                Venue = e.Venue,
                StartsAtUtc = e.StartsAtUtc,
                TotalCapacity = e.TotalCapacity,
                Status = e.Status.ToString()
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<EventSummary>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    // Counted from the tickets themselves, never from a stored counter.
    public async Task<EventAvailability> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken)
    {
        Event? existing = await db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        if (existing == null)
        {
            throw new EventNotFoundException(eventId);
        }

        List<TierAvailability> tiers = await db.PricingTiers
            .Where(t => t.EventId == eventId)
            .OrderByDescending(t => t.PriceAmount)
            .Select(t => new TierAvailability
            {
                PricingTierId = t.Id,
                Name = t.Name,
                PriceAmount = t.PriceAmount,
                PriceCurrency = t.PriceCurrency,
                Allocation = t.Allocation,
                Sold = db.Tickets.Count(x => x.PricingTierId == t.Id && x.Status == TicketStatus.Sold),
                Available = db.Tickets.Count(x => x.PricingTierId == t.Id && x.Status == TicketStatus.Available)
            })
            .ToListAsync(cancellationToken);

        int totalAvailable = 0;

        foreach (TierAvailability tier in tiers)
        {
            totalAvailable += tier.Available;
        }

        return new EventAvailability
        {
            EventId = eventId,
            Status = existing.Status.ToString(),
            TotalAvailable = totalAvailable,
            PricingTiers = tiers
        };
    }

    // Two queries: event totals, then the per-tier breakdown.
    public async Task<SalesReport> GetSalesReportAsync(Guid eventId, CancellationToken cancellationToken)
    {
        SalesReport? report = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new SalesReport
            {
                EventId = e.Id,
                EventName = e.Name,
                TicketsSold = db.Tickets.Count(t => t.EventId == e.Id && t.Status == TicketStatus.Sold),
                TicketsAvailable = db.Tickets.Count(t => t.EventId == e.Id && t.Status == TicketStatus.Available),
                TotalRevenue = db.Purchases
                    .Where(p => p.EventId == e.Id && p.Status == PurchaseStatus.Completed)
                    .Sum(p => (decimal?)p.TotalAmount) ?? 0m,
                Currency = db.PricingTiers
                    .Where(t => t.EventId == e.Id)
                    .Select(t => t.PriceCurrency)
                    .FirstOrDefault() ?? string.Empty,
                PurchaseCount = db.Purchases.Count(p => p.EventId == e.Id && p.Status == PurchaseStatus.Completed)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (report == null)
        {
            throw new EventNotFoundException(eventId);
        }

        report.PricingTiers = await db.PricingTiers
            .Where(t => t.EventId == eventId)
            .OrderByDescending(t => t.PriceAmount)
            .Select(t => new TierSales
            {
                PricingTierId = t.Id,
                Name = t.Name,
                PriceAmount = t.PriceAmount,
                Allocation = t.Allocation,
                Sold = db.Tickets.Count(x => x.PricingTierId == t.Id && x.Status == TicketStatus.Sold),
                Available = db.Tickets.Count(x => x.PricingTierId == t.Id && x.Status == TicketStatus.Available),
                Revenue = db.PurchaseItems
                    .Where(i => i.PricingTierId == t.Id)
                    .Sum(i => (decimal?)i.ItemTotal) ?? 0m
            })
            .ToListAsync(cancellationToken);

        return report;
    }

    public static void ValidateEventCapacity(
        Guid eventId, IReadOnlyList<PricingTierInput> tiers, int totalCapacity)
    {
        int allocated = TotalAllocation(tiers);

        if (allocated != totalCapacity)
        {
            throw new CapacityViolationException(
                eventId, $"Tier allocations total {allocated} but capacity is {totalCapacity}.");
        }
    }

    public static void RequireAllocationCoversSold(Guid eventId, string tierName, int allocation, int sold)
    {
        if (allocation < sold)
        {
            throw new CapacityViolationException(
                eventId,
                $"Tier '{tierName}' has {sold} tickets sold; allocation cannot be reduced to {allocation}.");
        }
    }

    public static void RequireTierIsUnsoldBeforeRemoval(Guid eventId, string tierName, int sold)
    {
        if (sold > 0)
        {
            throw new CapacityViolationException(
                eventId, $"Tier '{tierName}' has {sold} tickets sold and cannot be removed.");
        }
    }

    public static int TotalAllocation(IReadOnlyList<PricingTierInput> tiers)
    {
        int allocated = 0;

        foreach (PricingTierInput tier in tiers)
        {
            allocated += tier.Allocation;
        }

        return allocated;
    }

    // Locks the event row for the life of the transaction
    private async Task<Event> LockEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        List<Event> rows = await db.Events
            .FromSql($"SELECT * FROM events WHERE id = {eventId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            throw new EventNotFoundException(eventId);
        }

        return rows[0];
    }

    private async Task ResizeTierAsync(
        Guid eventId,
        PricingTier current,
        PricingTierInput target,
        CancellationToken cancellationToken)
    {
        int sold = await SoldCountAsync(current.Id, cancellationToken);

        RequireAllocationCoversSold(eventId, current.Name, target.Allocation, sold);

        int total = await db.Tickets.CountAsync(t => t.PricingTierId == current.Id, cancellationToken);

        if (target.Allocation > total)
        {
            int highestOrdinal = await db.Tickets
                .Where(t => t.PricingTierId == current.Id)
                .MaxAsync(t => (int?)t.SeatOrdinal, cancellationToken) ?? 0;

            await CreateTicketsAsync(
                eventId, current.Id, NewTicketIds(target.Allocation - total), highestOrdinal, cancellationToken);
            return;
        }

        if (target.Allocation < total)
        {
            int excess = total - target.Allocation;

            // Highest ordinals first, so the seats a buyer would get next are the
            // last to go. Ordinals may end up with gaps; the count is what matters.
            List<Guid> release = await db.Tickets
                .Where(t => t.PricingTierId == current.Id && t.Status == TicketStatus.Available)
                .OrderByDescending(t => t.SeatOrdinal)
                .Take(excess)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            // Status is re-checked in the delete itself. The event row lock keeps other
            // updates out, but a sale never touches that row, so one can commit
            // between the read above and this delete.
            int deleted = await db.Tickets
                .Where(t => release.Contains(t.Id) && t.Status == TicketStatus.Available)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted != excess)
            {
                throw new CapacityViolationException(
                    eventId,
                    $"Tier '{current.Name}' had {excess} unsold tickets to release but only {deleted} were still unsold.");
            }
        }
    }

    private Task<int> SoldCountAsync(Guid pricingTierId, CancellationToken cancellationToken)
    {
        return db.Tickets.CountAsync(
            t => t.PricingTierId == pricingTierId 
                 && t.Status == TicketStatus.Sold, cancellationToken);
    }


    private static PricingTier? FindByName(List<PricingTier> tiers, string name)
    {
        foreach (PricingTier tier in tiers)
        {
            if (string.Equals(tier.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tier;
            }
        }

        return null;
    }

    private static PricingTierInput? FindByName(List<PricingTierInput> tiers, string name)
    {
        foreach (PricingTierInput tier in tiers)
        {
            if (string.Equals(tier.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tier;
            }
        }

        return null;
    }

    private async Task CreateTicketsAsync(
        Guid eventId,
        Guid pricingTierId,
        List<Guid> ticketIds,
        int ordinalOffset,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            CreateTicketsSql,
            [eventId, pricingTierId, ordinalOffset, ticketIds.ToArray()],
            cancellationToken);
    }

    private static List<Guid> NewTicketIds(int count)
    {
        List<Guid> ids = new(count);

        for (int i = 0; i < count; i++)
        {
            ids.Add(Guid.CreateVersion7());
        }

        return ids;
    }
}
