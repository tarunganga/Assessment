using System.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ripple.Treasury.Assessment.Infrastructure;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Infrastructure.Enums;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.Services;

public class TicketPurchaseService(TicketingDbContext db) : ITicketPurchaseService
{
    // Takes the first N unsold seats nobody else is holding, and skips the ones
    // that are taken rather than waiting. No LINQ equivalent, so it is raw SQL -
    // run through EF, which enlists it in the ambient transaction.
    private const string SellSql =
        """
        WITH selling AS (
            SELECT id
            FROM tickets
            WHERE pricing_tier_id = {0}
              AND status = 'Available'
            ORDER BY id
            LIMIT {1}
            FOR UPDATE SKIP LOCKED
        )
        UPDATE tickets t
        SET status = 'Sold',
            purchase_id = {2},
            sold_at = now()
        FROM selling s
        WHERE t.id = s.id
        RETURNING t.id AS "Value";
        """;

    public async Task<PurchaseResult> PurchaseAsync(
        PurchaseTicketsInput input, CancellationToken cancellationToken)
    {
        // Compute a fingerprint of the purchase request
        string fingerprint = ComputeFingerprint(input);

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Get a blocking advisory lock using idempotency key
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({input.IdempotencyKey}))", cancellationToken);

        Purchase? existing = await db.Purchases
            .FirstOrDefaultAsync(p => p.IdempotencyKey == input.IdempotencyKey, cancellationToken);

        if (existing != null)
        {
            await transaction.RollbackAsync(cancellationToken);

            // Caller tried to replay a purchase with a different fingerprint
            if (existing.RequestFingerprint != fingerprint)
            {
                throw new IdempotencyKeyConflictException(input.IdempotencyKey);
            }

            // Return the existing purchase - instead of replaying
            return new PurchaseResult
            {
                PurchaseId = existing.Id,
                IsReplay = true
            };
        }

        // Get the event for which the purchase is being made
        Event purchaseEvent = await LockEventAsync(input.EventId, cancellationToken);

        // Check if the event is published (i.e. ready for purchase)
        if (purchaseEvent.Status != EventStatus.Published)
        {
            throw new InvalidEventStateException(
                input.EventId, purchaseEvent.Status.ToString(), "purchasing");
        }

        // Get the pricing tiers for the event
        List<PricingTier> tiers = await db.PricingTiers
            .Where(t => t.EventId == input.EventId)
            .ToListAsync(cancellationToken);

        List<PurchaseItemInput> lines = input.PurchaseItems;

        Purchase purchase = new()
        {
            Id = Guid.CreateVersion7(),
            EventId = input.EventId,
            IdempotencyKey = input.IdempotencyKey,
            RequestFingerprint = fingerprint,
            PurchaserEmail = input.PurchaserEmail,
            Status = PurchaseStatus.Completed
        };

        // Copies current tier price onto each item, so repricing later cannot change
        ApplyPricing(purchase, input.EventId, tiers, lines);

        db.Purchases.Add(purchase);

        // The purchase row must exist before tickets reference it.
        await db.SaveChangesAsync(cancellationToken);

        foreach (PurchaseItemInput line in lines)
        {
            List<Guid> sold = await db.Database
                .SqlQueryRaw<Guid>(SellSql, line.PricingTierId, line.Quantity, purchase.Id)
                .ToListAsync(cancellationToken);

            // Throwing here rolls back the whole purchase, including tiers already
            // sold. Buying two tiers is one deal, not two.
            if (sold.Count != line.Quantity)
            {
                throw new InsufficientInventoryException(
                    line.PricingTierId, line.Quantity, sold.Count);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new PurchaseResult
        {
            PurchaseId = purchase.Id,
            IsReplay = false
        };
    }

    // Copies current tier price onto each item, so repricing later cannot change
    // what this purchase was worth.
    public static void ApplyPricing(
        Purchase purchase,
        Guid eventId,
        IReadOnlyList<PricingTier> tiers,
        IReadOnlyList<PurchaseItemInput> lines)
    {
        decimal total = 0m;
        string currency = string.Empty;

        foreach (PurchaseItemInput line in lines)
        {
            PricingTier tier = FindTier(tiers, line.PricingTierId)
                ?? throw new PricingTierNotFoundException(eventId, line.PricingTierId);

            if (currency.Length == 0)
            {
                currency = tier.PriceCurrency;
            }
            else if (!string.Equals(currency, tier.PriceCurrency, StringComparison.Ordinal))
            {
                throw new CapacityViolationException(
                    eventId,
                    $"Tiers in one purchase must share a currency; found '{currency}' and '{tier.PriceCurrency}'.");
            }

            decimal itemTotal = tier.PriceAmount * line.Quantity;
            total += itemTotal;

            purchase.Items.Add(new PurchaseItem
            {
                Id = Guid.CreateVersion7(),
                PurchaseId = purchase.Id,
                PricingTierId = tier.Id,
                Quantity = line.Quantity,
                UnitPrice = tier.PriceAmount,
                ItemTotal = itemTotal
            });
        }

        purchase.TotalAmount = total;
        purchase.Currency = currency;
    }

    private static PricingTier? FindTier(IReadOnlyList<PricingTier> tiers, Guid pricingTierId)
    {
        foreach (PricingTier tier in tiers)
        {
            if (tier.Id == pricingTierId)
            {
                return tier;
            }
        }

        return null;
    }

    public async Task<PurchaseDetail> GetAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        PurchaseDetail? detail = await db.Purchases
            .Where(p => p.Id == purchaseId)
            .Select(p => new PurchaseDetail
            {
                Id = p.Id,
                EventId = p.EventId,
                PurchaserEmail = p.PurchaserEmail,
                TotalAmount = p.TotalAmount,
                Currency = p.Currency,
                Status = p.Status.ToString(),
                CreatedAt = p.CreatedAt,
                Items = p.Items
                    .Select(i => new PurchaseItemDetail
                    {
                        PricingTierId = i.PricingTierId,
                        PricingTierName = i.PricingTier!.Name,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        ItemTotal = i.ItemTotal
                    })
                    .ToList(),
                TicketIds = db.Tickets
                    .Where(t => t.PurchaseId == p.Id)
                    .OrderBy(t => t.Id)
                    .Select(t => t.Id)
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return detail ?? throw new PurchaseNotFoundException(purchaseId);
    }

// Identifies what was asked for. Two requests meaning the same thing hash the
    // same, so a retry replays instead of being reported as a conflict.
    public static string ComputeFingerprint(PurchaseTicketsInput input)
    {
        List<PurchaseItemInput> ordered = [.. input.PurchaseItems];
        ordered.Sort((left, right) => left.PricingTierId.CompareTo(right.PricingTierId));

        StringBuilder builder = new();
        builder.Append(input.EventId.ToString());
        builder.Append('|');
        builder.Append(input.PurchaserEmail.Trim().ToLowerInvariant());

        foreach (PurchaseItemInput item in ordered)
        {
            builder.Append('|');
            builder.Append(item.PricingTierId.ToString());
            builder.Append(':');
            builder.Append(item.Quantity);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // FOR SHARE holds the event row for the rest of the transaction. It does not
    // block other purchases, but it does block the FOR UPDATE that cancelling or
    // updating an event takes - so the status the caller checks cannot change under it.
    private async Task<Event> LockEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        List<Event> rows = await db.Events
            .FromSql($"SELECT * FROM events WHERE id = {eventId} FOR SHARE")
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            throw new EventNotFoundException(eventId);
        }

        return rows[0];
    }
}
