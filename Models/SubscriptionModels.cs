namespace RepairPartsPro.Models;

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string SubscriptionTier { get; set; } = "Basic";
    public DateTime? SubscriptionExpiresAtUtc { get; set; }
    public bool IsSubscriptionActive { get; set; }
}

public sealed class SubscriptionTier
{
    public string Name { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public int SearchesPerDay { get; set; }
    public int AlertsAllowed { get; set; }
    public bool ExportCsvAllowed { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class UpdatePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class UpgradeSubscriptionRequest
{
    public string TierName { get; set; } = string.Empty;
}

public sealed class DeleteAccountRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
}

public sealed class UserSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TierName { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
}
