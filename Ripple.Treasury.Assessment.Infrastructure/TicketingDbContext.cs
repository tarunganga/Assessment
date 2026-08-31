using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ripple.Treasury.Assessment.Infrastructure.Entities;
using Ripple.Treasury.Assessment.Infrastructure.Enums;

namespace Ripple.Treasury.Assessment.Infrastructure;


public sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events { get; set; } = null!;
    public DbSet<PricingTier> PricingTiers { get; set; } = null!;
    public DbSet<Purchase> Purchases { get; set; } = null!;
    public DbSet<PurchaseItem> PurchaseItems { get; set; } = null!;
    public DbSet<Ticket> Tickets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEvents(modelBuilder);
        ConfigurePricingTiers(modelBuilder);
        ConfigurePurchases(modelBuilder);
        ConfigurePurchaseItems(modelBuilder);
        ConfigureTickets(modelBuilder);
    }

    private static void ConfigureEvents(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<Event> entity = modelBuilder.Entity<Event>();

        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_events_status", "status IN ('Draft', 'Published', 'Cancelled')");
            table.HasCheckConstraint("ck_events_capacity_positive", "total_capacity > 0");
            table.HasCheckConstraint("ck_events_name_not_blank", "length(btrim(name)) > 0");
            table.HasCheckConstraint("ck_events_venue_not_blank", "length(btrim(venue)) > 0");
        });

        entity.Property(x => x.Status).HasConversion<string>().HasDefaultValue(EventStatus.Draft);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        entity.HasMany(x => x.Tiers)
            .WithOne(x => x.Event)
            .HasForeignKey(x => x.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.StartsAtUtc)
            .HasDatabaseName("ix_events_starts_at")
            .HasFilter("status = 'Published'");
    }

    private static void ConfigurePricingTiers(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<PricingTier> entity = modelBuilder.Entity<PricingTier>();

        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_pricing_tiers_price_non_negative", "price_amount >= 0");
            table.HasCheckConstraint("ck_pricing_tiers_allocation_positive", "allocation > 0");
            table.HasCheckConstraint("ck_pricing_tiers_currency_iso", "price_currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_pricing_tiers_name_not_blank", "length(btrim(name)) > 0");
        });

        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        entity.HasIndex(x => new { x.EventId, x.Name }).IsUnique().HasDatabaseName("uq_pricing_tiers_event_name");
        entity.HasIndex(x => x.EventId).HasDatabaseName("ix_pricing_tiers_event");
    }

    private static void ConfigurePurchases(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<Purchase> entity = modelBuilder.Entity<Purchase>();

        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_purchases_status", "status IN ('Completed', 'Cancelled')");
            table.HasCheckConstraint("ck_purchases_total_non_negative", "total_amount >= 0");
            table.HasCheckConstraint("ck_purchases_currency_iso", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint(
                "ck_purchases_email_shape",
                @"purchaser_email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'");
        });

        entity.Property(x => x.Status).HasConversion<string>().HasDefaultValue(PurchaseStatus.Completed);
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        entity.HasOne(x => x.Event)
            .WithMany()
            .HasForeignKey(x => x.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasMany(x => x.Items)
            .WithOne(x => x.Purchase)
            .HasForeignKey(x => x.PurchaseId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("uq_purchases_idempotency_key");
        entity.HasIndex(x => x.EventId).HasDatabaseName("ix_purchases_event");
        entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_purchases_created").IsDescending();
    }

    private static void ConfigurePurchaseItems(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<PurchaseItem> entity = modelBuilder.Entity<PurchaseItem>();

        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_purchase_items_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_purchase_items_price_non_negative", "unit_price >= 0");
            table.HasCheckConstraint("ck_purchase_items_total_consistent", "item_total = unit_price * quantity");
        });

        entity.HasOne(x => x.PricingTier)
            .WithMany()
            .HasForeignKey(x => x.PricingTierId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(x => new { x.PurchaseId, x.PricingTierId })
            .IsUnique()
            .HasDatabaseName("uq_purchase_items_purchase_tier");

        entity.HasIndex(x => x.PurchaseId).HasDatabaseName("ix_purchase_items_purchase");
        entity.HasIndex(x => x.PricingTierId).HasDatabaseName("ix_purchase_items_tier");
    }

    private static void ConfigureTickets(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<Ticket> entity = modelBuilder.Entity<Ticket>();

        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_tickets_status", "status IN ('Available', 'Sold')");
            table.HasCheckConstraint(
                "ck_tickets_sold_has_purchase",
                """
                (status = 'Available' AND purchase_id IS NULL     AND sold_at IS NULL)
                OR (status = 'Sold'      AND purchase_id IS NOT NULL AND sold_at IS NOT NULL)
                """);
        });

        entity.Property(x => x.Status).HasConversion<string>().HasDefaultValue(TicketStatus.Available);

        entity.HasOne(x => x.Event)
            .WithMany()
            .HasForeignKey(x => x.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.PricingTier)
            .WithMany()
            .HasForeignKey(x => x.PricingTierId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Purchase)
            .WithMany()
            .HasForeignKey(x => x.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        // Covers the sale: the unsold rows of a tier, in the order they are taken.
        entity.HasIndex(x => new { x.PricingTierId, x.Id }, "ix_tickets_available")
            .HasDatabaseName("ix_tickets_available")
            .HasFilter("status = 'Available'");

        entity.HasIndex(x => x.PurchaseId)
            .HasDatabaseName("ix_tickets_purchase")
            .HasFilter("purchase_id IS NOT NULL");

        entity.HasIndex(x => new { x.EventId, x.PricingTierId }).HasDatabaseName("ix_tickets_event_tier");
    }
}
