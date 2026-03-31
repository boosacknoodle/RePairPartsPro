namespace RepairPartsPro.Services;

public sealed class AuthProtectionOptions
{
    public int ObservationWindowMinutes { get; set; } = 15;
    public int CooldownAfterFailures { get; set; } = 3;
    public int CooldownSeconds { get; set; } = 30;
    public int LockoutAfterFailures { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
