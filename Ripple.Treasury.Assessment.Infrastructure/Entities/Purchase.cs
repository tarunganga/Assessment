using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Ripple.Treasury.Assessment.Infrastructure.Enums;

namespace Ripple.Treasury.Assessment.Infrastructure.Entities;

[Table("purchases")]
public class Purchase
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Column("event_id")]
    public Guid EventId { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [Column("request_fingerprint", TypeName = "char(64)")]
    public string RequestFingerprint { get; set; } = string.Empty;

    [Required]
    [MaxLength(320)]
    [Column("purchaser_email")]
    public string PurchaserEmail { get; set; } = string.Empty;

    [Precision(19, 4)]
    [Column("total_amount")]
    public decimal TotalAmount { get; set; }

    [Required]
    [Column("currency", TypeName = "char(3)")]
    public string Currency { get; set; } = string.Empty;

    [Column("status")]
    public PurchaseStatus Status { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation properties
    public Event? Event { get; set; }
    public List<PurchaseItem> Items { get; set; } = [];
}
