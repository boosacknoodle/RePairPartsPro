namespace RepairPartsPro.Models;

public sealed class RejectedPartListing
{
    public string Title { get; set; } = string.Empty;
    public string Marketplace { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Reason { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; set; }
}
