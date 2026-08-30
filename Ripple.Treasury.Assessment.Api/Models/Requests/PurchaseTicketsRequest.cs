using System.ComponentModel.DataAnnotations;

namespace Ripple.Treasury.Assessment.Api.Models.Requests;

public class PurchaseTicketsRequest : IValidatableObject
{
    [Required]
    [EmailAddress]
    [StringLength(320, MinimumLength = 3)]
    public string PurchaserEmail { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public List<PurchaseItemRequest> PurchaseItems { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        HashSet<Guid> tiers = [];

        foreach (PurchaseItemRequest item in PurchaseItems)
        {
            if (!tiers.Add(item.PricingTierId))
            {
                yield return new ValidationResult(
                    "Each pricing tier may appear only once per purchase.", [nameof(PurchaseItems)]);
                break;
            }
        }
    }
}

public class PurchaseItemRequest
{
    [Required]
    public Guid PricingTierId { get; set; }

    [Range(1, 50)]
    public int Quantity { get; set; }
}
