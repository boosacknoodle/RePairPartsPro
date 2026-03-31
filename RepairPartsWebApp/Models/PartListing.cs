namespace RepairPartsWebApp.Models;

public sealed class PartListing
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string SellerName { get; set; } = "";
    public double SellerFeedback { get; set; }
    public int Score { get; set; }
    public List<string> PartNumbers { get; set; } = new();
    public List<string> Reasons { get; set; } = new();
    public string Marketplace { get; set; } = "eBay";
    public string ImageUrl { get; set; } = "";
    public List<string> ImageUrls { get; set; } = new();
    public string ListingUrl { get; set; } = "";
    public bool IsEstimatedPrice { get; set; }
    public decimal BestPrice { get; set; }
    public string BestPriceMarketplace { get; set; } = "";
    public bool IsBestPrice => Math.Abs(Price - BestPrice) < 0.01m;
}
