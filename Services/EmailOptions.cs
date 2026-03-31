namespace RepairPartsPro.Services;

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Resend";
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string AppBaseUrl { get; set; } = "http://localhost:5002";
}
