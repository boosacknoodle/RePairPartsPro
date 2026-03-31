namespace RepairPartsPro.Services;

public interface IEmailSender
{
    Task SendPasswordResetAsync(string toEmail, string resetUrl, CancellationToken cancellationToken = default);
}
