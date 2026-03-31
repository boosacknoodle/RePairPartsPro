using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using RepairPartsPro.Models;

namespace RepairPartsPro.Services;

public interface IMarketplaceQuoteFetcher
{
    Task<MarketplaceQuote?> TryFetchQuoteAsync(VendorPartOffer offer, CancellationToken cancellationToken = default);
}

public sealed class MarketplaceQuoteFetcher : IMarketplaceQuoteFetcher
{
    private static readonly Regex GenericPriceRegex = new(@"\$\s*([0-9]{1,4}(?:,[0-9]{3})*(?:\.[0-9]{2})?)", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketplaceQuoteFetcher> _logger;
    private readonly EbayApiOptions _ebay;
    private readonly AmazonApiOptions _amazon;
    private readonly MarketplaceEndpointApiOptions _neweggApi;
    private readonly MarketplaceEndpointApiOptions _tigerDirectApi;
    private readonly MarketplaceEndpointApiOptions _microCenterApi;
    private readonly bool _disableHtmlFallback;
    private string? _cachedEbayToken;
    private DateTime _cachedEbayTokenExpiresUtc;

    public MarketplaceQuoteFetcher(
        HttpClient httpClient,
        ILogger<MarketplaceQuoteFetcher> logger,
        IOptions<MarketplaceApiOptions> apiOptions,
        IHostEnvironment environment)
    {
        _httpClient = httpClient;
        _logger = logger;
        _ebay = apiOptions.Value.Ebay;
        _amazon = apiOptions.Value.Amazon;
        _neweggApi = apiOptions.Value.Newegg;
        _tigerDirectApi = apiOptions.Value.TigerDirect;
        _microCenterApi = apiOptions.Value.MicroCenter;
        _disableHtmlFallback = environment.IsProduction() && apiOptions.Value.DisableHtmlFallbackInProduction;
    }

    public async Task<MarketplaceQuote?> TryFetchQuoteAsync(VendorPartOffer offer, CancellationToken cancellationToken = default)
    {
        try
        {
            var marketplace = offer.Marketplace.Trim();
            var query = BuildQuery(offer);

            if (string.Equals(marketplace, "eBay", StringComparison.OrdinalIgnoreCase))
            {
                var apiQuote = await TryFetchEbayApiQuoteAsync(query, cancellationToken);
                if (apiQuote is not null)
                {
                    apiQuote.Marketplace = offer.Marketplace;
                    return apiQuote;
                }
            }

            if (string.Equals(marketplace, "Amazon", StringComparison.OrdinalIgnoreCase))
            {
                var amazonQuote = await TryFetchAmazonApiQuoteAsync(query, cancellationToken);
                if (amazonQuote is not null)
                {
                    amazonQuote.Marketplace = offer.Marketplace;
                    return amazonQuote;
                }
            }

            if (string.Equals(marketplace, "Newegg", StringComparison.OrdinalIgnoreCase))
            {
                var neweggApiQuote = await TryFetchConfiguredEndpointQuoteAsync(_neweggApi, query, "NeweggEndpointApi", cancellationToken);
                if (neweggApiQuote is not null)
                {
                    neweggApiQuote.Marketplace = offer.Marketplace;
                    return neweggApiQuote;
                }
            }

            if (string.Equals(marketplace, "TigerDirect", StringComparison.OrdinalIgnoreCase))
            {
                var tigerApiQuote = await TryFetchConfiguredEndpointQuoteAsync(_tigerDirectApi, query, "TigerDirectEndpointApi", cancellationToken);
                if (tigerApiQuote is not null)
                {
                    tigerApiQuote.Marketplace = offer.Marketplace;
                    return tigerApiQuote;
                }
            }

            if (string.Equals(marketplace, "MicroCenter", StringComparison.OrdinalIgnoreCase))
            {
                var microCenterApiQuote = await TryFetchConfiguredEndpointQuoteAsync(_microCenterApi, query, "MicroCenterEndpointApi", cancellationToken);
                if (microCenterApiQuote is not null)
                {
                    microCenterApiQuote.Marketplace = offer.Marketplace;
                    return microCenterApiQuote;
                }
            }

            if (_disableHtmlFallback)
            {
                return null;
            }

            var url = BuildSearchUrl(marketplace, query);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "RepairPartsPro/1.0 (+https://localhost)");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Quote fetch failed for {Marketplace}: HTTP {Code}", marketplace, (int)response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var parsed = TryParsePrice(html, marketplace);
            if (!parsed.HasValue)
            {
                return null;
            }

            return new MarketplaceQuote
            {
                Marketplace = marketplace,
                ListingUrl = url,
                Price = parsed.Value.value,
                Currency = "USD",
                SourcePriceText = parsed.Value.raw,
                SourceType = "HtmlParse",
                RetrievedAtUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quote fetch failed for {Marketplace} {Part}", offer.Marketplace, offer.BasePartNumber);
            return null;
        }
    }

    private async Task<MarketplaceQuote?> TryFetchEbayApiQuoteAsync(string query, CancellationToken cancellationToken)
    {
        if (!_ebay.Enabled || string.IsNullOrWhiteSpace(_ebay.ClientId) || string.IsNullOrWhiteSpace(_ebay.ClientSecret))
        {
            return null;
        }

        var token = await GetEbayAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var baseUrl = string.Equals(_ebay.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
            ? "https://api.sandbox.ebay.com"
            : "https://api.ebay.com";

        var url = $"{baseUrl}/buy/browse/v1/item_summary/search?q={Uri.EscapeDataString(query)}&limit=5";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-EBAY-C-ENDUSERCTX", "contextualLocation=country=US");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("eBay API quote fetch failed HTTP {Code}", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("itemSummaries", out var itemSummaries) || itemSummaries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        decimal? bestPrice = null;
        string listingUrl = string.Empty;
        foreach (var item in itemSummaries.EnumerateArray())
        {
            if (!item.TryGetProperty("price", out var priceNode))
            {
                continue;
            }

            if (!priceNode.TryGetProperty("value", out var valueNode) || valueNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!decimal.TryParse(valueNode.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            if (parsed < 30m || parsed > 10000m)
            {
                continue;
            }

            if (bestPrice is null || parsed < bestPrice.Value)
            {
                bestPrice = parsed;
                listingUrl = item.TryGetProperty("itemWebUrl", out var webUrl) && webUrl.ValueKind == JsonValueKind.String
                    ? webUrl.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        if (!bestPrice.HasValue)
        {
            return null;
        }

        return new MarketplaceQuote
        {
            Marketplace = "eBay",
            ListingUrl = listingUrl,
            Price = bestPrice.Value,
            Currency = "USD",
            SourcePriceText = bestPrice.Value.ToString("0.00", CultureInfo.InvariantCulture),
            SourceType = "EbayBrowseApi",
            RetrievedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<MarketplaceQuote?> TryFetchAmazonApiQuoteAsync(string query, CancellationToken cancellationToken)
    {
        if (!_amazon.Enabled
            || string.IsNullOrWhiteSpace(_amazon.AccessKey)
            || string.IsNullOrWhiteSpace(_amazon.SecretKey)
            || string.IsNullOrWhiteSpace(_amazon.PartnerTag)
            || string.IsNullOrWhiteSpace(_amazon.Host)
            || string.IsNullOrWhiteSpace(_amazon.Region))
        {
            return null;
        }

        var payload = new
        {
            PartnerTag = _amazon.PartnerTag,
            PartnerType = "Associates",
            Keywords = query,
            SearchIndex = "All",
            ItemCount = 5,
            Resources = new[]
            {
                "ItemInfo.Title",
                "Offers.Listings.Price",
                "DetailPageURL"
            }
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string service = "ProductAdvertisingAPI";
        const string target = "com.amazon.paapi5.v1.ProductAdvertisingAPIv1.SearchItems";
        const string canonicalUri = "/paapi5/searchitems";

        var canonicalHeaders = string.Join("\n", new[]
        {
            "content-encoding:amz-1.0",
            "content-type:application/json; charset=utf-8",
            $"host:{_amazon.Host}",
            $"x-amz-date:{amzDate}",
            $"x-amz-target:{target}"
        }) + "\n";

        const string signedHeaders = "content-encoding;content-type;host;x-amz-date;x-amz-target";
        var payloadHash = Sha256Hex(payloadJson);
        var canonicalRequest = string.Join("\n", new[]
        {
            "POST",
            canonicalUri,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash
        });

        const string algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{_amazon.Region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", new[]
        {
            algorithm,
            amzDate,
            credentialScope,
            Sha256Hex(canonicalRequest)
        });

        var signingKey = GetSignatureKey(_amazon.SecretKey, dateStamp, _amazon.Region, service);
        var signature = HmacSha256Hex(signingKey, stringToSign);

        var authorization =
            $"{algorithm} Credential={_amazon.AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_amazon.Host}{canonicalUri}");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "amz-1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Amazon PA-API quote fetch failed HTTP {Code}", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("SearchResult", out var searchResult)
            || !searchResult.TryGetProperty("Items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        decimal? bestPrice = null;
        string bestUrl = string.Empty;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Offers", out var offersNode)
                || !offersNode.TryGetProperty("Listings", out var listingsNode)
                || listingsNode.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var listing in listingsNode.EnumerateArray())
            {
                if (!listing.TryGetProperty("Price", out var priceNode)
                    || !priceNode.TryGetProperty("Amount", out var amountNode)
                    || amountNode.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var parsed = amountNode.GetDecimal();
                if (parsed < 30m || parsed > 10000m)
                {
                    continue;
                }

                if (bestPrice is null || parsed < bestPrice.Value)
                {
                    bestPrice = parsed;
                    bestUrl = item.TryGetProperty("DetailPageURL", out var detailUrl) && detailUrl.ValueKind == JsonValueKind.String
                        ? detailUrl.GetString() ?? string.Empty
                        : string.Empty;
                }
            }
        }

        if (!bestPrice.HasValue)
        {
            return null;
        }

        return new MarketplaceQuote
        {
            Marketplace = "Amazon",
            ListingUrl = bestUrl,
            Price = bestPrice.Value,
            Currency = "USD",
            SourcePriceText = bestPrice.Value.ToString("0.00", CultureInfo.InvariantCulture),
            SourceType = "AmazonPaApi",
            RetrievedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<MarketplaceQuote?> TryFetchConfiguredEndpointQuoteAsync(
        MarketplaceEndpointApiOptions options,
        string query,
        string sourceType,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.EndpointTemplate))
        {
            return null;
        }

        var url = options.EndpointTemplate.Replace("{query}", Uri.EscapeDataString(query), StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(options.ApiKeyHeader, options.ApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("price", out var priceNode))
        {
            return null;
        }

        var price = priceNode.ValueKind switch
        {
            JsonValueKind.Number => priceNode.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(priceNode.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };

        if (price < 30m || price > 10000m)
        {
            return null;
        }

        var listingUrl = json.RootElement.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String
            ? urlNode.GetString() ?? string.Empty
            : string.Empty;
        var currency = json.RootElement.TryGetProperty("currency", out var currencyNode) && currencyNode.ValueKind == JsonValueKind.String
            ? currencyNode.GetString() ?? "USD"
            : "USD";

        return new MarketplaceQuote
        {
            Price = price,
            Currency = currency,
            ListingUrl = listingUrl,
            SourcePriceText = price.ToString("0.00", CultureInfo.InvariantCulture),
            SourceType = sourceType,
            RetrievedAtUtc = DateTime.UtcNow
        };
    }

    private static string Sha256Hex(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        return ToLowerHex(bytes);
    }

    private static string HmacSha256Hex(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return ToLowerHex(bytes);
    }

    private static byte[] HmacSha256(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + key);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, regionName);
        var kService = HmacSha256(kRegion, serviceName);
        return HmacSha256(kService, "aws4_request");
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private async Task<string?> GetEbayAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedEbayToken) && _cachedEbayTokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return _cachedEbayToken;
        }

        var baseUrl = string.Equals(_ebay.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
            ? "https://api.sandbox.ebay.com"
            : "https://api.ebay.com";

        var tokenUrl = $"{baseUrl}/identity/v1/oauth2/token";
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_ebay.ClientId}:{_ebay.ClientSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://api.ebay.com/oauth/api_scope/buy.browse"
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("eBay token request failed HTTP {Code}", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("access_token", out var tokenNode) || tokenNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var expiresIn = 7200;
        if (json.RootElement.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpires))
        {
            expiresIn = parsedExpires;
        }

        _cachedEbayToken = tokenNode.GetString();
        _cachedEbayTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        return _cachedEbayToken;
    }

    private static string BuildQuery(VendorPartOffer offer)
    {
        return string.Join(' ', new[]
        {
            offer.Brand,
            offer.Model,
            offer.BasePartNumber,
            offer.PartType
        }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static string BuildSearchUrl(string marketplace, string query)
    {
        var q = Uri.EscapeDataString(query);
        return marketplace.ToLowerInvariant() switch
        {
            "amazon" => $"https://www.amazon.com/s?k={q}",
            "ebay" => $"https://www.ebay.com/sch/i.html?_nkw={q}",
            "newegg" => $"https://www.newegg.com/p/pl?d={q}",
            "tigerdirect" => $"https://www.google.com/search?q={Uri.EscapeDataString("site:tigerdirect.com " + query)}",
            "microcenter" => $"https://www.microcenter.com/search/search_results.aspx?Ntt={q}",
            _ => string.Empty
        };
    }

    private static (decimal value, string raw)? TryParsePrice(string html, string marketplace)
    {
        var normalized = html.Replace("\u00a0", " ", StringComparison.Ordinal).Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        foreach (Match match in GenericPriceRegex.Matches(normalized))
        {
            var raw = match.Groups[1].Value;
            if (!decimal.TryParse(raw, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price))
            {
                continue;
            }

            if (price < 30m || price > 10000m)
            {
                continue;
            }

            return (price, raw);
        }

        return null;
    }
}
