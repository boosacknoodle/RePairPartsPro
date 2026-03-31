namespace RepairPartsPro.Models;

public sealed class HardPartIntelItem
{
    public int Id { get; set; }
    public string PartName { get; set; } = string.Empty;
    public string DeviceFamily { get; set; } = string.Empty;
    public string PainReason { get; set; } = string.Empty;
    public string SearchHint { get; set; } = string.Empty;
    public string ForumSignalSource { get; set; } = string.Empty;
    public int PainScore { get; set; }
    public bool IsScamSensitive { get; set; }
}
