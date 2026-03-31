using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Hosting;
using RepairPartsWebApp.Data;
using RepairPartsWebApp.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace RepairPartsWebApp.Services;

public sealed class MarketplaceSearchService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> CustomerCacheKeys = new();

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;

    public MarketplaceSearchService(AppDbContext db, IMemoryCache cache, IWebHostEnvironment environment, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _cache = cache;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PartSearchResult> SearchAsync(PartSearchRequest request, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(request);
        RegisterCustomerCacheKey(request.CustomerId, cacheKey);
        if (_cache.TryGetValue(cacheKey, out PartSearchResult? cached) && cached is not null)
        {
            return cached;
        }

        var brand = request.Brand.Trim();
        var model = request.Model.Trim();
        var partType = request.PartType.Trim();
        var blacklistedSellers = await _db.SellerBlacklistEntries.AsNoTracking()
            .Where(x => x.CustomerId == request.CustomerId)
            .Select(x => x.SellerName)
            .ToListAsync(cancellationToken);
        var globallyBlacklistedSellers = await _db.SellerReputations.AsNoTracking()
            .Where(x => x.IsGloballyBlacklisted)
            .Select(x => x.SellerName)
            .ToListAsync(cancellationToken);
        var blockedSet = new HashSet<string>(blacklistedSellers, StringComparer.OrdinalIgnoreCase);
        foreach (var seller in globallyBlacklistedSellers)
        {
            blockedSet.Add(seller);
        }

        var query = _db.Parts.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(brand))
        {
            query = query.Where(x => EF.Functions.Like(x.Brand, $"%{brand}%") || EF.Functions.Like(x.Title, $"%{brand}%"));
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            query = query.Where(x => EF.Functions.Like(x.Model, $"%{model}%") || EF.Functions.Like(x.Title, $"%{model}%"));
        }

        if (!string.IsNullOrWhiteSpace(partType))
        {
            query = query.Where(x => EF.Functions.Like(x.PartType, $"%{partType}%") || EF.Functions.Like(x.Title, $"%{partType}%"));
        }

        var parts = await query
            .OrderBy(x => x.Title)
            .Take(120)
            .ToListAsync(cancellationToken);
        parts = parts.Where(x => !blockedSet.Contains(x.SellerName)).ToList();

        if (parts.Count == 0)
        {
            parts = await _db.Parts.AsNoTracking()
                .OrderBy(x => x.Brand)
                .ThenBy(x => x.Model)
                .Take(20)
                .ToListAsync(cancellationToken);
            parts = parts.Where(x => !blockedSet.Contains(x.SellerName)).ToList();
        }

        var basePartNumbers = parts
            .Select(x => GetBasePartNumber(x.PartNumber))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var canonicalLookup = await _db.CanonicalListingLinks.AsNoTracking()
            .Where(x => basePartNumbers.Contains(x.BasePartNumber))
            .ToListAsync(cancellationToken);

        var canonicalMap = canonicalLookup
            .GroupBy(x => $"{x.BasePartNumber}|{x.Marketplace}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAtUtc).First().Url, StringComparer.OrdinalIgnoreCase);

        var listings = parts.Select(x =>
        {
            var gallery = BuildImageGallery(x.PartNumber, x.PartType, x.ImageUrl);
            var basePartNumber = GetBasePartNumber(x.PartNumber);
            var key = $"{basePartNumber}|{x.Marketplace}";
            var canonicalUrl = canonicalMap.TryGetValue(key, out var matchedUrl) ? matchedUrl : null;
            var resolvedListingUrl = !string.IsNullOrWhiteSpace(canonicalUrl) ? canonicalUrl : BuildListingUrl(x);

            return new PartListing
            {
                Id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{x.Brand}|{x.Model}|{x.Title}|{x.Price:F2}")),
                Title = x.Title,
                Price = x.Price,
                Currency = x.Currency,
                SellerName = x.SellerName,
                SellerFeedback = x.SellerFeedback,
                Score = Math.Clamp(x.QualityScore / 20, 1, 5),
                PartNumbers = new List<string> { x.PartNumber },
                Reasons = BuildReasons(x),
                Marketplace = x.Marketplace,
                ImageUrl = gallery.FirstOrDefault() ?? x.ImageUrl,
                ImageUrls = gallery,
                ListingUrl = resolvedListingUrl,
                IsEstimatedPrice = !IsDirectMarketplaceListingUrl(resolvedListingUrl)
            };
        }).ToList();

        await EnrichWithLivePricesAsync(listings, cancellationToken);
        SetBestPrices(listings);

        var result = new PartSearchResult
        {
            Brand = brand,
            Model = model,
            PartType = partType,
            RetrievedAtUtc = DateTime.UtcNow,
            Listings = listings
        };

        _cache.Set(cacheKey, result, CacheTtl);

        return result;
    }

    private async Task EnrichWithLivePricesAsync(List<PartListing> listings, CancellationToken cancellationToken)
    {
        if (listings.Count == 0)
        {
            return;
        }

        foreach (var listing in listings.Take(40))
        {
            var url = listing.ListingUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var cacheKey = $"listing-resolution|v5|{url}";
            if (_cache.TryGetValue(cacheKey, out ListingResolution? cached) && cached is not null)
            {
                if (!string.IsNullOrWhiteSpace(cached.Url))
                {
                    listing.ListingUrl = cached.Url;
                }

                if (cached.Price.HasValue && cached.Price.Value > 0)
                {
                    listing.Price = cached.Price.Value;
                }

                listing.IsEstimatedPrice = cached.IsEstimated;
                continue;
            }

            var resolved = await ResolveListingAsync(url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved.Url))
            {
                listing.ListingUrl = resolved.Url;
            }

            if (resolved.Price.HasValue && resolved.Price.Value > 0)
            {
                listing.Price = resolved.Price.Value;
            }

            listing.IsEstimatedPrice = resolved.IsEstimated;
            _cache.Set(cacheKey, resolved, TimeSpan.FromMinutes(30));
        }
    }

    private async Task<ListingResolution> ResolveListingAsync(string listingUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(listingUrl, UriKind.Absolute, out var uri))
        {
            return ListingResolution.Estimated(listingUrl, null);
        }

        if (IsDirectMarketplaceListingUrl(uri))
        {
            var price = await TryFetchLivePriceAsync(listingUrl, cancellationToken);
            return ListingResolution.Exact(listingUrl, price);
        }

        var searchResolution = await TryResolveFromSearchPageAsync(uri, cancellationToken);
        if (!searchResolution.IsEstimated && !string.IsNullOrWhiteSpace(searchResolution.Url))
        {
            var finalPrice = searchResolution.Price;
            if (!finalPrice.HasValue || finalPrice.Value <= 0)
            {
                finalPrice = await TryFetchLivePriceAsync(searchResolution.Url, cancellationToken);
            }

            if (!finalPrice.HasValue || finalPrice.Value <= 0)
            {
                return ListingResolution.Estimated(searchResolution.Url, null);
            }

            return ListingResolution.Exact(searchResolution.Url, finalPrice);
        }

        return ListingResolution.Estimated(listingUrl, searchResolution.Price);
    }

    private async Task<ListingResolution> TryResolveFromSearchPageAsync(Uri searchUri, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            using var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ListingResolution.Estimated(searchUri.ToString(), null);
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                return ListingResolution.Estimated(searchUri.ToString(), null);
            }

            if (searchUri.Host.Contains("ebay.", StringComparison.OrdinalIgnoreCase))
            {
                var blockMatch = Regex.Match(
                    html,
                    "<li[^>]*class=\"s-item[^\"]*\"[\\s\\S]*?<a[^>]*class=\"s-item__link\"[^>]*href=\"(?<url>https?://[^\"]+/itm/[^\"?]+(?:\\?[^\"]*)?)\"[\\s\\S]*?<span[^>]*class=\"s-item__price\"[^>]*>[^0-9]*(?<price>[0-9][0-9,\\.]{0,15})",
                    RegexOptions.IgnoreCase);
                if (blockMatch.Success)
                {
                    var url = blockMatch.Groups["url"].Value.Trim();
                    var parsed = TryParsePrice(blockMatch.Groups["price"].Value, out var ebayPrice) ? ebayPrice : (decimal?)null;
                    return ListingResolution.Exact(url, parsed);
                }

                var urlMatch = Regex.Match(html, "https?://[^\"']+/itm/[^\"'\\s<]+", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                {
                    return ListingResolution.Exact(urlMatch.Value.Trim(), null);
                }
            }

            if (searchUri.Host.Contains("amazon.", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveAmazonFromSearchHtml(searchUri.Host, html, out var amazonResolution))
                {
                    return amazonResolution;
                }

                var fallbackHtml = await TryFetchHtmlWithHttpClientAsync(searchUri, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fallbackHtml)
                    && TryResolveAmazonFromSearchHtml(searchUri.Host, fallbackHtml, out amazonResolution))
                {
                    return amazonResolution;
                }
            }
        }
        catch
        {
            return ListingResolution.Estimated(searchUri.ToString(), null);
        }

        return ListingResolution.Estimated(searchUri.ToString(), null);
    }

    private async Task<decimal?> TryFetchLivePriceAsync(string listingUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(listingUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!IsDirectMarketplaceListingUrl(uri))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (TryExtractMarketplacePrice(uri, html, out var price))
            {
                return price;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsDirectMarketplaceListingUrl(Uri uri)
    {
        var host = uri.Host ?? string.Empty;
        var path = uri.AbsolutePath ?? string.Empty;
        var query = uri.Query ?? string.Empty;

        if (host.Contains("amazon.", StringComparison.OrdinalIgnoreCase))
        {
            var isProductPath = path.Contains("/dp/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/gp/product/", StringComparison.OrdinalIgnoreCase);
            var isSearchPage = path.Equals("/s", StringComparison.OrdinalIgnoreCase)
                || query.Contains("k=", StringComparison.OrdinalIgnoreCase);

            return isProductPath && !isSearchPage;
        }

        if (host.Contains("ebay.", StringComparison.OrdinalIgnoreCase))
        {
            var isItemPath = path.Contains("/itm/", StringComparison.OrdinalIgnoreCase);
            var isSearchPage = path.Contains("/sch/", StringComparison.OrdinalIgnoreCase)
                || query.Contains("_nkw=", StringComparison.OrdinalIgnoreCase);

            return isItemPath && !isSearchPage;
        }

        return false;
    }

    private static bool IsDirectMarketplaceListingUrl(string listingUrl)
    {
        return Uri.TryCreate(listingUrl, UriKind.Absolute, out var uri) && IsDirectMarketplaceListingUrl(uri);
    }

    private sealed class ListingResolution
    {
        public string Url { get; init; } = string.Empty;
        public decimal? Price { get; init; }
        public bool IsEstimated { get; init; }

        public static ListingResolution Exact(string url, decimal? price)
        {
            return new ListingResolution
            {
                Url = url,
                Price = price,
                IsEstimated = false
            };
        }

        public static ListingResolution Estimated(string url, decimal? price)
        {
            return new ListingResolution
            {
                Url = url,
                Price = price,
                IsEstimated = true
            };
        }
    }

    private static bool TryExtractPrice(string html, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        // JSON-LD offer price commonly present on product pages.
        var match = Regex.Match(html, "\"price\"\\s*:\\s*\"(?<value>[0-9][0-9,\\.]{0,15})\"", RegexOptions.IgnoreCase);
        if (match.Success && TryParsePrice(match.Groups["value"].Value, out price))
        {
            return true;
        }

        // eBay fallback for currentPrice JSON fragment.
        match = Regex.Match(html, "\"currentPrice\"\\s*:\\s*\\{[^}]*\"value\"\\s*:\\s*\"(?<value>[0-9][0-9,\\.]{0,15})\"", RegexOptions.IgnoreCase);
        if (match.Success && TryParsePrice(match.Groups["value"].Value, out price))
        {
            return true;
        }

        // Search results fallback (eBay/Amazon)
        match = Regex.Match(html, "[$£€]\\s*(?<value>[0-9][0-9,\\.]{0,15})", RegexOptions.IgnoreCase);
        if (match.Success && TryParsePrice(match.Groups["value"].Value, out price))
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractMarketplacePrice(Uri uri, string html, out decimal price)
    {
        price = 0m;
        var host = uri.Host ?? string.Empty;

        if (host.Contains("amazon.", StringComparison.OrdinalIgnoreCase))
        {
            var offscreen = Regex.Match(html, "class=\"a-offscreen\"[^>]*>[^0-9]*(?<value>[0-9][0-9,\\.]{0,15})<", RegexOptions.IgnoreCase);
            if (offscreen.Success && TryParsePrice(offscreen.Groups["value"].Value, out price))
            {
                return true;
            }

            var payAmount = Regex.Match(html, "\"priceToPay\"[\\s\\S]{0,300}?\"priceAmount\"\\s*:\\s*(?<value>[0-9][0-9,\\.]{0,15})", RegexOptions.IgnoreCase);
            if (payAmount.Success && TryParsePrice(payAmount.Groups["value"].Value, out price))
            {
                return true;
            }

            return false;
        }

        if (host.Contains("ebay.", StringComparison.OrdinalIgnoreCase))
        {
            var currentPrice = Regex.Match(html, "\"currentPrice\"\\s*:\\s*\\{[^}]*\"value\"\\s*:\\s*\"(?<value>[0-9][0-9,\\.]{0,15})\"", RegexOptions.IgnoreCase);
            if (currentPrice.Success && TryParsePrice(currentPrice.Groups["value"].Value, out price))
            {
                return true;
            }
        }

        return TryExtractPrice(html, out price);
    }

    private static bool TryResolveAmazonFromSearchHtml(string host, string html, out ListingResolution resolution)
    {
        resolution = ListingResolution.Estimated($"https://{host}/", null);

        var asinMatches = Regex.Matches(html, "data-asin=\"(?<asin>[A-Z0-9]{10})\"", RegexOptions.IgnoreCase);
        if (asinMatches.Count == 0)
        {
            return false;
        }

        string? firstDirectUrl = null;
        for (var i = 0; i < asinMatches.Count; i++)
        {
            var match = asinMatches[i];
            var asin = match.Groups["asin"].Value.Trim();
            if (string.IsNullOrWhiteSpace(asin))
            {
                continue;
            }

            var start = match.Index;
            var nextStart = i + 1 < asinMatches.Count ? asinMatches[i + 1].Index : html.Length;
            var length = Math.Max(0, Math.Min(nextStart - start, 12000));
            if (length == 0)
            {
                continue;
            }

            var segment = html.Substring(start, length);
            var hrefMatch = Regex.Match(segment, "href=\"(?<href>/[^\"\\s]*?/dp/(?<asin2>[A-Z0-9]{10})[^\"\\s]*)\"", RegexOptions.IgnoreCase);
            var resolvedAsin = hrefMatch.Success ? hrefMatch.Groups["asin2"].Value.Trim() : asin;
            if (string.IsNullOrWhiteSpace(resolvedAsin))
            {
                continue;
            }

            var directUrl = $"https://{host}/dp/{resolvedAsin}";
            firstDirectUrl ??= directUrl;

            var offscreenPrice = Regex.Match(segment, "class=\"a-offscreen\"[^>]*>[^0-9]*(?<value>[0-9][0-9,\\.]{0,15})<", RegexOptions.IgnoreCase);
            if (offscreenPrice.Success && TryParsePrice(offscreenPrice.Groups["value"].Value, out var parsedOffscreenPrice))
            {
                resolution = ListingResolution.Exact(directUrl, parsedOffscreenPrice);
                return true;
            }

            var whole = Regex.Match(segment, "class=\"a-price-whole\"[^>]*>(?<value>[0-9,]+)<", RegexOptions.IgnoreCase);
            var fraction = Regex.Match(segment, "class=\"a-price-fraction\"[^>]*>(?<value>[0-9]{2})<", RegexOptions.IgnoreCase);
            if (whole.Success)
            {
                var composite = whole.Groups["value"].Value;
                if (fraction.Success)
                {
                    composite = $"{composite}.{fraction.Groups["value"].Value}";
                }

                var parsed = TryParsePrice(composite, out var amazonPrice) ? amazonPrice : (decimal?)null;
                resolution = ListingResolution.Exact(directUrl, parsed);
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstDirectUrl))
        {
            resolution = ListingResolution.Exact(firstDirectUrl, null);
            return true;
        }

        return false;
    }

    private static async Task<string> TryFetchHtmlWithHttpClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParsePrice(string input, out decimal value)
    {
        value = 0m;
        var normalized = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        normalized = normalized.Replace(",", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public void InvalidateCustomerCache(int customerId)
    {
        if (!CustomerCacheKeys.TryGetValue(customerId, out var keys))
        {
            return;
        }

        foreach (var key in keys.Keys)
        {
            _cache.Remove(key);
        }

        CustomerCacheKeys.TryRemove(customerId, out _);
    }

    public void InvalidateAllCache()
    {
        foreach (var customerBucket in CustomerCacheKeys)
        {
            foreach (var key in customerBucket.Value.Keys)
            {
                _cache.Remove(key);
            }
        }

        CustomerCacheKeys.Clear();
    }

    private static void SetBestPrices(List<PartListing> listings)
    {
        foreach (var group in listings.GroupBy(x => x.Title))
        {
            var lowest = group.OrderBy(x => x.Price).First();
            foreach (var item in group)
            {
                item.BestPrice = lowest.Price;
                item.BestPriceMarketplace = lowest.Marketplace;
            }
        }
    }

    private static List<string> BuildReasons(Part part)
    {
        var reasons = new List<string>();

        if (part.SellerFeedback >= 99)
        {
            reasons.Add("Excellent seller feedback (99%+)");
        }
        else if (part.SellerFeedback >= 98)
        {
            reasons.Add("Outstanding seller feedback (98%+)");
        }
        else
        {
            reasons.Add("Verified seller");
        }

        if (part.QualityScore >= 95)
        {
            reasons.Add("Top quality score");
        }
        else if (part.QualityScore >= 90)
        {
            reasons.Add("High quality verified");
        }

        reasons.Add("Authentic part verified");
        return reasons;
    }

    private static string BuildCacheKey(PartSearchRequest request)
    {
        return $"{request.CustomerId}|{request.Brand.Trim().ToLowerInvariant()}|{request.Model.Trim().ToLowerInvariant()}|{request.PartType.Trim().ToLowerInvariant()}";
    }

    private static string GetBasePartNumber(string partNumber)
    {
        var raw = partNumber ?? string.Empty;
        return raw.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private static string BuildListingUrl(Part part)
    {
        var rawPartNumber = part.PartNumber ?? string.Empty;
        var basePartNumber = GetBasePartNumber(rawPartNumber);
        var normalizedBase = basePartNumber.Trim();
        var query = string.IsNullOrWhiteSpace(normalizedBase)
            ? part.Title.Trim()
            : $"{normalizedBase} {part.Title.Trim()}";

        var encoded = Uri.EscapeDataString(query);
        if (part.Marketplace.Equals("Amazon", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.amazon.com/s?k={encoded}&i=electronics";
        }

        if (part.Marketplace.Equals("eBay", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.ebay.com/sch/i.html?_nkw={encoded}&_sacat=0";
        }

        return string.IsNullOrWhiteSpace(part.ListingUrl)
            ? $"https://www.google.com/search?q={encoded}"
            : part.ListingUrl;
    }

    private List<string> BuildImageGallery(string partNumber, string partType, string primaryImage)
    {
        var gallery = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(primaryImage) && seen.Add(primaryImage))
        {
            gallery.Add(primaryImage);
        }

        var typeVariants = GetPartTypeVariantImages(partType);
        foreach (var variant in typeVariants)
        {
            AddIfExists(variant, gallery, seen);
            if (gallery.Count >= 3)
            {
                break;
            }
        }

        if (gallery.Count == 0)
        {
            AddIfExists("/images/parts/default-1.jpg", gallery, seen);
            AddIfExists("/images/parts/default-2.jpg", gallery, seen);
            AddIfExists("/images/parts/default-3.jpg", gallery, seen);
        }

        if (gallery.Count == 1)
        {
            AddIfExists("/images/parts/default-1.jpg", gallery, seen);
        }

        return gallery;
    }

    private static List<string> GetPartTypeVariantImages(string partType)
    {
        var type = (partType ?? string.Empty).Trim().ToLowerInvariant();
        return type switch
        {
            "battery" => new List<string>
            {
                "/images/parts/battery-1.jpg",
                "/images/parts/battery-2.jpg",
                "/images/parts/battery-3.jpg",
                "/images/parts/battery-4.jpg"
            },
            "ssd" => new List<string>
            {
                "/images/parts/ssd-1.jpg",
                "/images/parts/ssd-2.jpg",
                "/images/parts/ssd-3.jpg",
                "/images/parts/ssd-4.jpg"
            },
            "motherboard" => new List<string>
            {
                "/images/parts/motherboard-1.jpg",
                "/images/parts/motherboard-2.jpg",
                "/images/parts/motherboard-3.jpg",
                "/images/parts/motherboard-4.jpg"
            },
            "ram" => new List<string>
            {
                "/images/parts/ram-1.jpg",
                "/images/parts/ram-2.jpg",
                "/images/parts/ram-3.jpg",
                "/images/parts/ram-4.jpg"
            },
            "cpu cooler" => new List<string>
            {
                "/images/parts/cpu-cooler-1.jpg",
                "/images/parts/cpu-cooler-2.jpg",
                "/images/parts/cpu-cooler-3.jpg",
                "/images/parts/cpu-cooler-4.jpg"
            },
            "mouse" => new List<string>
            {
                "/images/parts/mouse-1.jpg",
                "/images/parts/mouse-2.jpg",
                "/images/parts/mouse-3.jpg",
                "/images/parts/mouse-4.jpg"
            },
            "keyboard" => new List<string>
            {
                "/images/parts/keyboard-1.jpg",
                "/images/parts/keyboard-2.jpg",
                "/images/parts/keyboard-3.jpg",
                "/images/parts/keyboard-4.jpg"
            },
            "power supply" => new List<string>
            {
                "/images/parts/power-supply-1.jpg",
                "/images/parts/power-supply-2.jpg",
                "/images/parts/power-supply-3.jpg",
                "/images/parts/power-supply-4.jpg"
            },
            _ => new List<string>
            {
                "/images/parts/default-1.jpg",
                "/images/parts/default-2.jpg",
                "/images/parts/default-3.jpg"
            }
        };
    }

    private void AddIfExists(string webPath, List<string> gallery, HashSet<string> seen)
    {
        var webRoot = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return;
        }

        var relative = webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(webRoot, relative);
        if (File.Exists(fullPath) && seen.Add(webPath))
        {
            gallery.Add(webPath);
        }
    }

    private void RegisterCustomerCacheKey(int customerId, string cacheKey)
    {
        var bucket = CustomerCacheKeys.GetOrAdd(customerId, _ => new ConcurrentDictionary<string, byte>());
        bucket.TryAdd(cacheKey, 0);
    }
}
