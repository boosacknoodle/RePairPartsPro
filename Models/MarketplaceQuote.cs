namespace RepairPartsPro.Models;

public sealed class MarketplaceQuote
{
    public string Marketplace { get; set; } = string.Empty;
    public string ListingUrl { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string SourcePriceText { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public DateTime RetrievedAtUtc { get; set; }
}
