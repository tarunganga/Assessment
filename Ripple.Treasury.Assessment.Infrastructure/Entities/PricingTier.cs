using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ripple.Treasury.Assessment.Infrastructure.Entities;

[Table("pricing_tiers")]
public class PricingTier
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Column("event_id")]
    public Guid EventId { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Precision(19, 4)]
    [Column("price_amount")]
    public decimal PriceAmount { get; set; }

    [Required]
    [Column("price_currency", TypeName = "char(3)")]
    public string PriceCurrency { get; set; } = string.Empty;

    [Column("allocation")]
    public int Allocation { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation properties
    public Event? Event { get; set; }
}
