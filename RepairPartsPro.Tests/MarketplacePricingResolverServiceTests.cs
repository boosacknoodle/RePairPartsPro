using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RepairPartsPro.Models;
using RepairPartsPro.Services;
using Xunit;

namespace RepairPartsPro.Tests;

public sealed class MarketplacePricingResolverServiceTests
{
    [Fact]
    public async Task SearchAsync_IncludesAllMarketplaces_WhenCertified()
    {
        var service = await CreateServiceAsync();

        var result = await service.SearchAsync(new PartSearchRequest
        {
            CustomerId = 1,
            Brand = "ASUS",
            Model = "B550-F",
            PartType = "Motherboard"
        });

        Assert.Contains(result.Listings, x => x.Marketplace == "Amazon");
        Assert.Contains(result.Listings, x => x.Marketplace == "eBay");
        Assert.Contains(result.Listings, x => x.Marketplace == "Newegg");
        Assert.Contains(result.Listings, x => x.Marketplace == "TigerDirect");
        Assert.Contains(result.Listings, x => x.Marketplace == "MicroCenter");
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsCertifiedAndGenuineListings()
    {
        var service = await CreateServiceAsync();

        var result = await service.SearchAsync(new PartSearchRequest
        {
            CustomerId = 1,
            Brand = "Dell",
            Model = "XPS 15",
            PartType = "Battery"
        });

        Assert.All(result.Listings, listing =>
        {
            Assert.True(listing.IsPriceVerified);
            Assert.True(listing.IsGenuinePart);
            Assert.False(listing.IsEstimatedPrice);
            Assert.Equal(string.Empty, listing.ListingUrl);
        });

        Assert.DoesNotContain(result.Listings, x => x.Price >= 200m);
    }

    [Fact]
    public async Task SearchAsync_ReturnsThreeDistinctImagesPerListing()
    {
        var service = await CreateServiceAsync();

        var result = await service.SearchAsync(new PartSearchRequest
        {
            CustomerId = 1,
            Brand = "ASUS",
            Model = "B550-F",
            PartType = "Motherboard"
        });

        Assert.NotEmpty(result.Listings);
        Assert.All(result.Listings, listing =>
        {
            Assert.Equal(3, listing.ImageUrls.Count);
            Assert.Equal(3, listing.ImageUrls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(listing.ImageUrl, listing.ImageUrls[0]);
        });
    }

    private static async Task<MarketplacePricingResolverService> CreateServiceAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"repairpartspro-tests-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}"
            })
            .Build();

        var store = new VendorDataStore(config);
        await store.InitializeAsync();
        await store.SeedIfEmptyAsync();

        return new MarketplacePricingResolverService(
            new MemoryCache(new MemoryCacheOptions()),
            store,
            new PriceCertificationEngine(Options.Create(new CertificationPolicyOptions
            {
                ApiOnlyMode = true,
                RequireOfficialApiSource = true,
                OfficialApiSourceTypes = ["EbayBrowseApi", "AmazonPaApi", "NeweggEndpointApi", "TigerDirectEndpointApi"]
            })),
            new FakeQuoteFetcher());
    }

    private sealed class FakeQuoteFetcher : IMarketplaceQuoteFetcher
    {
        public Task<MarketplaceQuote?> TryFetchQuoteAsync(VendorPartOffer offer, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MarketplaceQuote?>(new MarketplaceQuote
            {
                Marketplace = offer.Marketplace,
                ListingUrl = $"https://example.invalid/{offer.Marketplace}/{offer.BasePartNumber}",
                Price = offer.Price,
                Currency = offer.Currency,
                SourcePriceText = offer.Price.ToString("0.00"),
                SourceType = "EbayBrowseApi",
                RetrievedAtUtc = DateTime.UtcNow
            });
        }
    }
}
