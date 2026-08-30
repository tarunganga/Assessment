using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;

namespace Ripple.Treasury.Assessment.UnitTests.Services;

public class TicketPurchaseServiceFingerprintTests
{
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid TierA = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid TierB = Guid.Parse("01900000-0000-7000-8000-0000000000b2");

    private static PurchaseTicketsInput Input(params (Guid Tier, int Quantity)[] items)
    {
        PurchaseTicketsInput input = new()
        {
            EventId = EventId,
            IdempotencyKey = "key-1",
            PurchaserEmail = "buyer@example.com"
        };

        foreach ((Guid tier, int quantity) in items)
        {
            input.PurchaseItems.Add(new PurchaseItemInput { PricingTierId = tier, Quantity = quantity });
        }

        return input;
    }

    [Fact]
    public void Item_order_does_not_change_the_fingerprint()
    {
        string first = TicketPurchaseService.ComputeFingerprint(Input((TierA, 2), (TierB, 3)));
        string second = TicketPurchaseService.ComputeFingerprint(Input((TierB, 3), (TierA, 2)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Email_case_and_surrounding_whitespace_are_normalised()
    {
        PurchaseTicketsInput messy = Input((TierA, 1));
        messy.PurchaserEmail = "  BUYER@Example.COM ";

        Assert.Equal(
            TicketPurchaseService.ComputeFingerprint(Input((TierA, 1))),
            TicketPurchaseService.ComputeFingerprint(messy));
    }

    [Fact]
    public void Different_quantity_changes_the_fingerprint()
    {
        Assert.NotEqual(
            TicketPurchaseService.ComputeFingerprint(Input((TierA, 2))),
            TicketPurchaseService.ComputeFingerprint(Input((TierA, 3))));
    }

    [Fact]
    public void Different_tier_changes_the_fingerprint()
    {
        Assert.NotEqual(
            TicketPurchaseService.ComputeFingerprint(Input((TierA, 2))),
            TicketPurchaseService.ComputeFingerprint(Input((TierB, 2))));
    }

    [Fact]
    public void Different_event_changes_the_fingerprint()
    {
        PurchaseTicketsInput other = Input((TierA, 2));
        other.EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");

        Assert.NotEqual(TicketPurchaseService.ComputeFingerprint(Input((TierA, 2))), TicketPurchaseService.ComputeFingerprint(other));
    }

    [Fact]
    public void Different_purchaser_changes_the_fingerprint()
    {
        PurchaseTicketsInput other = Input((TierA, 2));
        other.PurchaserEmail = "someone.else@example.com";

        Assert.NotEqual(TicketPurchaseService.ComputeFingerprint(Input((TierA, 2))), TicketPurchaseService.ComputeFingerprint(other));
    }

    [Fact]
    public void Idempotency_key_itself_is_not_part_of_the_fingerprint()
    {
        PurchaseTicketsInput other = Input((TierA, 2));
        other.IdempotencyKey = "a-completely-different-key";

        Assert.Equal(TicketPurchaseService.ComputeFingerprint(Input((TierA, 2))), TicketPurchaseService.ComputeFingerprint(other));
    }

    [Fact]
    public void Fingerprint_fits_the_char_64_column()
    {
        string fingerprint = TicketPurchaseService.ComputeFingerprint(Input((TierA, 1)));

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.Contains(c, "0123456789ABCDEF"));
    }
}
