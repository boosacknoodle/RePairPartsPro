namespace RepairPartsPro.Models;

public sealed class PartSearchRequest
{
    public int CustomerId { get; set; }
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PartType { get; set; } = string.Empty;
}
