namespace RepairPartsPro.Models;

public sealed class PartListing
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Marketplace { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string ListingUrl { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public List<string> ImageUrls { get; set; } = new();
    public bool IsEstimatedPrice { get; set; }
    public bool IsPriceVerified { get; set; }
    public bool IsGenuinePart { get; set; }
    public DateTime VerifiedAtUtc { get; set; }
    public string PriceSourceType { get; set; } = string.Empty;
    public string PriceSourceUrl { get; set; } = string.Empty;
    public string PriceSourceText { get; set; } = string.Empty;
    public bool IsHardToFind { get; set; }
    public int RarityScore { get; set; }
    public int AuthenticityScore { get; set; }
    public string TrustNote { get; set; } = string.Empty;
    public bool MatchedPainIntel { get; set; }
    public string PainIntelTag { get; set; } = string.Empty;
}
