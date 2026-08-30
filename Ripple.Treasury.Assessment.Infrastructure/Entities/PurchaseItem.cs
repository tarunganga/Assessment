using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ripple.Treasury.Assessment.Infrastructure.Entities;

[Table("purchase_items")]
public class PurchaseItem
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Column("purchase_id")]
    public Guid PurchaseId { get; set; }

    [Column("pricing_tier_id")]
    public Guid PricingTierId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Precision(19, 4)]
    [Column("unit_price")]
    public decimal UnitPrice { get; set; }

    [Precision(19, 4)]
    [Column("item_total")]
    public decimal ItemTotal { get; set; }

    // Navigation properties
    public Purchase? Purchase { get; set; }
    public PricingTier? PricingTier { get; set; }
}
