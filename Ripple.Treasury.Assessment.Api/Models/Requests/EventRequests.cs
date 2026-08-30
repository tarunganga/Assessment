using System.ComponentModel.DataAnnotations;

namespace Ripple.Treasury.Assessment.Api.Models.Requests;

public class SaveEventRequest : IValidatableObject
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Venue { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset StartsAtUtc { get; set; }

    [Range(1, 500_000)]
    public int TotalCapacity { get; set; }

    [Required]
    [MinLength(1)]
    public List<SavePricingTierRequest> PricingTiers { get; set; } = [];

    // Cross-field rules. Anything needing a query is a state rule and belongs in
    // the service, inside the transaction that enforces it.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        int allocated = 0;

        foreach (SavePricingTierRequest tier in PricingTiers)
        {
            allocated += tier.Allocation;
        }

        if (allocated != TotalCapacity)
        {
            yield return new ValidationResult(
                $"Pricing tier allocations total {allocated} but capacity is {TotalCapacity}.",
                [nameof(PricingTiers), nameof(TotalCapacity)]);
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (SavePricingTierRequest tier in PricingTiers)
        {
            if (!names.Add(tier.Name))
            {
                yield return new ValidationResult(
                    "Pricing tier names must be unique.", [nameof(PricingTiers)]);
                break;
            }
        }

        if (StartsAtUtc <= DateTimeOffset.UtcNow)
        {
            yield return new ValidationResult(
                "Event must start in the future.", [nameof(StartsAtUtc)]);
        }
    }
}

public class SavePricingTierRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "1000000")]
    public decimal PriceAmount { get; set; }

    [Required]
    [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Currency must be a three-letter ISO code.")]
    public string PriceCurrency { get; set; } = string.Empty;

    [Range(1, 500_000)]
    public int Allocation { get; set; }
}
