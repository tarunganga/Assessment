using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ripple.Treasury.Assessment.Infrastructure.Enums;

namespace Ripple.Treasury.Assessment.Infrastructure.Entities;

[Table("events")]
public class Event
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("venue")]
    public string Venue { get; set; } = string.Empty;

    [Column("starts_at_utc")]
    public DateTimeOffset StartsAtUtc { get; set; }

    [Column("total_capacity")]
    public int TotalCapacity { get; set; }

    [Column("status")]
    public EventStatus Status { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public List<PricingTier> Tiers { get; set; } = [];
}
