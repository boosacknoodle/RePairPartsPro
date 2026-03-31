using Microsoft.Extensions.Caching.Memory;
using RepairPartsPro.Models;

namespace RepairPartsPro.Services;

public sealed class MarketplacePricingResolverService
{
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxLivePriceAge = TimeSpan.FromHours(6);

    private readonly IMemoryCache _cache;
    private readonly VendorDataStore _dataStore;
    private readonly PriceCertificationEngine _certificationEngine;
    private readonly IMarketplaceQuoteFetcher _quoteFetcher;

    public MarketplacePricingResolverService(
        IMemoryCache cache,
        VendorDataStore dataStore,
        PriceCertificationEngine certificationEngine,
        IMarketplaceQuoteFetcher quoteFetcher)
    {
        _cache = cache;
        _dataStore = dataStore;
        _certificationEngine = certificationEngine;
        _quoteFetcher = quoteFetcher;
    }

    public async Task<PartSearchResult> SearchAsync(PartSearchRequest request, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildSearchCacheKey(request);
        if (_cache.TryGetValue(cacheKey, out PartSearchResult? cached) && cached is not null)
        {
            return cached;
        }

        var offers = await _dataStore.SearchOffersAsync(request, cancellationToken);
        var rejected = new List<RejectedPartListing>();
        var preRejectedOfferIds = new HashSet<int>();
        foreach (var offer in offers)
        {
            var liveQuote = await _quoteFetcher.TryFetchQuoteAsync(offer, cancellationToken);
            if (liveQuote is null)
            {
                continue;
            }

            if (!PriceConfidencePolicy.IsReasonableQuoteChange(offer.Price, liveQuote.Price))
            {
                var reason = "Fetched quote rejected as low-confidence";
                await _dataStore.SaveCertificationAsync(offer.Id, false, reason, DateTime.UtcNow, cancellationToken);
                rejected.Add(ToRejectedListing(offer, reason));
                preRejectedOfferIds.Add(offer.Id);
                continue;
            }

            await _dataStore.UpdateOfferQuoteAsync(offer.Id, liveQuote, cancellationToken);
            offer.Price = liveQuote.Price;
            offer.Currency = liveQuote.Currency;
            offer.SourceVerifiedAtUtc = liveQuote.RetrievedAtUtc;
            offer.SourceUrl = liveQuote.ListingUrl;
            offer.SourcePriceText = liveQuote.SourcePriceText;
            offer.SourceType = liveQuote.SourceType;
        }

        var certifiedListings = new List<PartListing>();
        var painIntelItems = await _dataStore.GetHardPartIntelAsync(40, cancellationToken);

        foreach (var offer in offers)
        {
            if (preRejectedOfferIds.Contains(offer.Id))
            {
                continue;
            }

            if (DateTime.UtcNow - offer.SourceVerifiedAtUtc > MaxLivePriceAge)
            {
                var reason = "Live price is stale";
                await _dataStore.SaveCertificationAsync(offer.Id, false, reason, DateTime.UtcNow, cancellationToken);
                rejected.Add(ToRejectedListing(offer, reason));
                continue;
            }

            var decision = _certificationEngine.Certify(offer, offers);
            await _dataStore.SaveCertificationAsync(offer.Id, decision.IsCertified, decision.Reason, DateTime.UtcNow, cancellationToken);
            if (!decision.IsCertified)
            {
                rejected.Add(ToRejectedListing(offer, decision.Reason));
                continue;
            }

            var painIntelMatch = MatchPainIntel(offer, painIntelItems);

            certifiedListings.Add(new PartListing
            {
                Id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{offer.BasePartNumber}|{offer.Marketplace}|{offer.Price}")),
                Title = offer.Title,
                Marketplace = offer.Marketplace,
                Price = offer.Price,
                Currency = offer.Currency,
                ListingUrl = string.Empty,
                ImageUrl = offer.ImageUrl1,
                ImageUrls =
                [
                    offer.ImageUrl1,
                    offer.ImageUrl2,
                    offer.ImageUrl3
                ],
                IsEstimatedPrice = false,
                IsPriceVerified = true,
                IsGenuinePart = true,
                VerifiedAtUtc = offer.SourceVerifiedAtUtc,
                PriceSourceType = offer.SourceType,
                PriceSourceUrl = offer.SourceUrl,
                PriceSourceText = offer.SourcePriceText,
                RarityScore = ComputeRarityScore(offer, offers, painIntelMatch is not null),
                AuthenticityScore = ComputeAuthenticityScore(offer, painIntelMatch is not null),
                TrustNote = BuildTrustNote(offer, painIntelMatch),
                MatchedPainIntel = painIntelMatch is not null,
                PainIntelTag = painIntelMatch?.PartName ?? string.Empty
            });

            var created = certifiedListings[^1];
            created.IsHardToFind = created.RarityScore >= 70;
        }

        var sortedListings = certifiedListings
            .OrderByDescending(x => x.IsHardToFind)
            .ThenBy(x => x.Price)
            .Take(20)
            .ToList();

        var result = new PartSearchResult
        {
            Brand = request.Brand,
            Model = request.Model,
            PartType = request.PartType,
            RetrievedAtUtc = DateTime.UtcNow,
            QualityPromise = "No repair part is out of reach: we run background pain-intel matching and certify only scam-screened genuine listings.",
            TotalOffersScanned = offers.Count,
            HardToFindMatches = sortedListings.Count(x => x.IsHardToFind),
            Listings = sortedListings,
            RejectedListings = rejected
                .OrderBy(x => x.Marketplace)
                .ThenBy(x => x.Price)
                .ToList()
        };

        _cache.Set(cacheKey, result, SearchCacheTtl);
        return result;
    }

    private static RejectedPartListing ToRejectedListing(VendorPartOffer offer, string reason)
    {
        return new RejectedPartListing
        {
            Title = offer.Title,
            Marketplace = offer.Marketplace,
            Price = offer.Price,
            Currency = offer.Currency,
            Reason = reason,
            SourceType = offer.SourceType,
            SourceUrl = offer.SourceUrl,
            EvaluatedAtUtc = DateTime.UtcNow
        };
    }

    private static string BuildSearchCacheKey(PartSearchRequest request)
    {
        return string.Join('|',
            request.CustomerId,
            request.Brand.Trim().ToLowerInvariant(),
            request.Model.Trim().ToLowerInvariant(),
            request.PartType.Trim().ToLowerInvariant());
    }

    private static int ComputeRarityScore(VendorPartOffer offer, IReadOnlyCollection<VendorPartOffer> peerOffers, bool matchedPainIntel)
    {
        var comparableCount = peerOffers.Count(x =>
            string.Equals(x.BasePartNumber, offer.BasePartNumber, StringComparison.OrdinalIgnoreCase));

        var score = 45;
        if (comparableCount <= 1)
        {
            score += 35;
        }
        else if (comparableCount == 2)
        {
            score += 22;
        }
        else if (comparableCount == 3)
        {
            score += 12;
        }

        if (!string.IsNullOrWhiteSpace(offer.BasePartNumber) && offer.BasePartNumber.Length >= 6)
        {
            score += 10;
        }

        var title = offer.Title.ToLowerInvariant();
        if (title.Contains("oem", StringComparison.Ordinal)
            || title.Contains("genuine", StringComparison.Ordinal)
            || title.Contains("original", StringComparison.Ordinal))
        {
            score += 8;
        }

        if (matchedPainIntel)
        {
            score += 15;
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int ComputeAuthenticityScore(VendorPartOffer offer, bool matchedPainIntel)
    {
        var score = 80;
        if (offer.IsGenuineSupplier)
        {
            score += 10;
        }

        var title = offer.Title.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(offer.BasePartNumber)
            && title.Contains(offer.BasePartNumber.ToLowerInvariant(), StringComparison.Ordinal))
        {
            score += 6;
        }

        if (title.Contains("oem", StringComparison.Ordinal)
            || title.Contains("genuine", StringComparison.Ordinal)
            || title.Contains("original", StringComparison.Ordinal))
        {
            score += 4;
        }

        if (matchedPainIntel)
        {
            score += 2;
        }

        return Math.Clamp(score, 0, 100);
    }

    private static string BuildTrustNote(VendorPartOffer offer, HardPartIntelItem? painIntelMatch)
    {
        var source = string.IsNullOrWhiteSpace(offer.SourceType) ? "verified source" : offer.SourceType;
        if (painIntelMatch is null)
        {
            return $"Certified genuine candidate from {source} with scam-screened title and cross-vendor price checks.";
        }

        return $"Certified genuine candidate from {source}; matched hard-part pain intel: {painIntelMatch.PartName}.";
    }

    private static HardPartIntelItem? MatchPainIntel(VendorPartOffer offer, IReadOnlyCollection<HardPartIntelItem> painIntelItems)
    {
        if (painIntelItems.Count == 0)
        {
            return null;
        }

        var haystack = string.Join(' ',
            offer.Title,
            offer.Brand,
            offer.Model,
            offer.PartType,
            offer.BasePartNumber)
            .ToLowerInvariant();

        foreach (var item in painIntelItems)
        {
            var tokens = ExtractTokens(item.PartName)
                .Concat(ExtractTokens(item.SearchHint))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var hitCount = tokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
            if (hitCount >= 2)
            {
                return item;
            }
        }

        return null;
    }

    private static IEnumerable<string> ExtractTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .ToLowerInvariant()
            .Split([' ', '/', '-', '(', ')', ',', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 4);
    }
}
