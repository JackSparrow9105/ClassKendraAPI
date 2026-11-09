using System.Net;
using System.Net.Mail;

namespace ClassKendraAPI.Features.Messages;

public interface IEmailSender
{
    /// <summary>False if it couldn't be sent (the reason is logged).</summary>
    Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct);
}

/// <summary>Development / not set up yet: nothing is sent, the email is written to the log (Azure → Log stream).</summary>
public class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        log.LogWarning("DEV EMAIL (not sent) to {To}: {Subject} | {Text}", to, subject, text.Replace("\n", " "));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Sends through any SMTP service (Brevo, Gmail, Zoho, Outlook…). Settings:
///   Email:Provider = Smtp, Email:Host, Email:Port (587), Email:User, Email:Password,
///   Email:From (a sender address the service allows), Email:FromName (default "ClassKendra").
/// Port 587 with STARTTLS. Port 465 (SSL from the start) is not supported by .NET's SmtpClient.
/// </summary>
public class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        try
        {
            using var client = new SmtpClient(config["Email:Host"], int.TryParse(config["Email:Port"], out var p) ? p : 587)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(config["Email:User"], config["Email:Password"]),
                Timeout = 20_000,
            };
            using var message = new MailMessage(
                new MailAddress(config["Email:From"]!, config["Email:FromName"] ?? "ClassKendra"),
                new MailAddress(to))
            {
                Subject = subject,
                Body = text,
                IsBodyHtml = false,
            };
            await client.SendMailAsync(message, ct);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or ArgumentException)
        {
            log.LogError(ex, "Email to {To} not sent ({Subject})", to, subject);
            return false;
        }
    }
}
