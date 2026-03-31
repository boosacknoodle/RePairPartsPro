using RepairPartsPro.Models;
using Microsoft.Extensions.Options;

namespace RepairPartsPro.Services;

public sealed class PriceCertificationEngine
{
    private static readonly HashSet<string> ApprovedMarketplaces =
    [
        "Amazon",
        "eBay",
        "Newegg",
        "TigerDirect",
        "MicroCenter"
    ];

    private static readonly string[] ScamSignals =
    [
        "for parts",
        "as-is",
        "not working",
        "untested",
        "broken",
        "read description",
        "compatible with",
        "replacement for",
        "aftermarket",
        "non-oem",
        "knockoff",
        "copy"
    ];

    private readonly CertificationPolicyOptions _policy;

    public PriceCertificationEngine(IOptions<CertificationPolicyOptions> policy)
    {
        _policy = policy.Value;
    }

    public CertificationDecision Certify(VendorPartOffer offer, IReadOnlyCollection<VendorPartOffer> peerOffers)
    {
        if (!ApprovedMarketplaces.Contains(offer.Marketplace))
        {
            return CertificationDecision.Fail("Marketplace not approved");
        }

        if (!offer.IsGenuineSupplier)
        {
            return CertificationDecision.Fail("Supplier not marked genuine");
        }

        if (offer.Price <= 0)
        {
            return CertificationDecision.Fail("Price is not valid");
        }

        if (_policy.ApiOnlyMode || _policy.RequireOfficialApiSource)
        {
            var allowed = _policy.OfficialApiSourceTypes ?? Array.Empty<string>();
            if (!allowed.Contains(offer.SourceType, StringComparer.OrdinalIgnoreCase))
            {
                return CertificationDecision.Fail("Offer source is not an official marketplace API");
            }
        }

        if (offer.SourceVerifiedAtUtc < DateTime.UtcNow.AddDays(-2))
        {
            return CertificationDecision.Fail("Source verification is stale");
        }

        var title = offer.Title.ToLowerInvariant();
        if (ScamSignals.Any(signal => title.Contains(signal, StringComparison.Ordinal)))
        {
            return CertificationDecision.Fail("Title has scam-risk wording");
        }

        var hasPartNumber = !string.IsNullOrWhiteSpace(offer.BasePartNumber)
            && title.Contains(offer.BasePartNumber.ToLowerInvariant(), StringComparison.Ordinal);
        var hasBrandModel = title.Contains(offer.Brand.ToLowerInvariant(), StringComparison.Ordinal)
            && title.Contains(offer.Model.ToLowerInvariant(), StringComparison.Ordinal);
        if (!hasPartNumber && !hasBrandModel)
        {
            return CertificationDecision.Fail("Title does not confirm requested identifiers");
        }

        var comparable = peerOffers
            .Where(x => x.IsGenuineSupplier)
            .Where(x => x.Price > 0)
            .Where(x => string.Equals(x.BasePartNumber, offer.BasePartNumber, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Price)
            .OrderBy(x => x)
            .ToList();

        if (comparable.Count < 2)
        {
            return CertificationDecision.Fail("Insufficient cross-vendor comparison data");
        }

        var median = comparable[comparable.Count / 2];
        var lowerBound = median * 0.85m;
        var upperBound = median * 1.15m;
        if (offer.Price < lowerBound || offer.Price > upperBound)
        {
            return CertificationDecision.Fail("Price outside cross-vendor tolerance");
        }

        return CertificationDecision.Pass();
    }

    public sealed record CertificationDecision(bool IsCertified, string Reason)
    {
        public static CertificationDecision Pass() => new(true, "Certified");
        public static CertificationDecision Fail(string reason) => new(false, reason);
    }
}
