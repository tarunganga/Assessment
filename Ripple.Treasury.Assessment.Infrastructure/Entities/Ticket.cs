using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ripple.Treasury.Assessment.Infrastructure.Enums;

namespace Ripple.Treasury.Assessment.Infrastructure.Entities;

[Table("tickets")]
public class Ticket
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("pricing_tier_id")]
    public Guid PricingTierId { get; set; }

    [Column("status")]
    public TicketStatus Status { get; set; }

    [Column("purchase_id")]
    public Guid? PurchaseId { get; set; }

    [Column("sold_at")]
    public DateTimeOffset? SoldAt { get; set; }

    // Navigation properties
    public Event? Event { get; set; }
    public PricingTier? PricingTier { get; set; }
    public Purchase? Purchase { get; set; }
}
