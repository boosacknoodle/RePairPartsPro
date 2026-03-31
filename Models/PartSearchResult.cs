namespace RepairPartsPro.Models;

public sealed class PartSearchResult
{
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PartType { get; set; } = string.Empty;
    public DateTime RetrievedAtUtc { get; set; }
    public string QualityPromise { get; set; } = "No repair part is out of reach: we hunt hard-to-find parts while enforcing strict anti-scam and genuine-only checks.";
    public int TotalOffersScanned { get; set; }
    public int HardToFindMatches { get; set; }
    public List<PartListing> Listings { get; set; } = new();
    public List<RejectedPartListing> RejectedListings { get; set; } = new();
}
