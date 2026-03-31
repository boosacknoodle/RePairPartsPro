namespace RepairPartsPro.Models;

public sealed class VendorPartOffer
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PartType { get; set; } = string.Empty;
    public string BasePartNumber { get; set; } = string.Empty;
    public string Marketplace { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string ListingUrl { get; set; } = string.Empty;
    public string ImageUrl1 { get; set; } = string.Empty;
    public string ImageUrl2 { get; set; } = string.Empty;
    public string ImageUrl3 { get; set; } = string.Empty;
    public bool IsGenuineSupplier { get; set; }
    public DateTime SourceVerifiedAtUtc { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string SourcePriceText { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
}
