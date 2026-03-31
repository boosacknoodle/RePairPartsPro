namespace RepairPartsPro.Services;

public sealed class PriceSyncOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public int BatchSize { get; set; } = 200;
}
