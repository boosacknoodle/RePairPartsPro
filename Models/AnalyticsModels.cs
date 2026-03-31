namespace RepairPartsPro.Models;

public sealed class SearchTermAggregate
{
    public string QueryText { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class AnalyticsSummary
{
    public int DaysWindow { get; set; }
    public int TotalSearches { get; set; }
    public int NoResultSearches { get; set; }
    public int UniqueUsers { get; set; }
    public int PlanClickEvents { get; set; }
    public List<SearchTermAggregate> TopSearches { get; set; } = new();
}
