using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

public static class EmailPurpose
{
    public const string Verify = "Verify";
    public const string Login = "Login";
}

public enum EmailSendResult { Sent, TooManyRequests, Failed }

/// <summary>
/// One-time codes by email, same rules as phone OTPs: 6 digits, only a hash is stored, a code works once,
/// 5 wrong tries kill it, at most 3 codes per 10 minutes per person.
/// </summary>
public class EmailCodeService(ClassKendraContext db, IConfiguration config, IEmailSender email, ILogger<EmailCodeService> log)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);   // email can take a minute to arrive
    private const int MaxAttempts = 5;
    private const int MaxSendsPer10Min = 3;

    public async Task<EmailSendResult> SendAsync(User user, string to, string purpose, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var recent = await db.EmailCodes.CountAsync(c => c.UserId == user.Id && c.CreatedAt >= now.AddMinutes(-10), ct);
        if (recent >= MaxSendsPer10Min) return EmailSendResult.TooManyRequests;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailCodes.Add(new EmailCode
        {
            UserId = user.Id,
            Email = to,
            Purpose = purpose,
            CodeHash = Hash(user.Id, purpose, to, code),
            ExpiresAt = now.Add(Lifetime),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        var (subject, text) = purpose == EmailPurpose.Login
            ? ($"{code} is your ClassKendra login code",
               $"Your ClassKendra login code is {code}\n\n" +
               $"Use it to sign in to the account with mobile number +91 {Mask.Phone(user.Phone)}. It works for 10 minutes.\n\n" +
               "Didn't ask for this? Someone may be trying to sign in to your account. Don't share this code with anyone — " +
               "your account is safe as long as nobody has it.")
            : ($"{code} is your code to confirm this email",
               $"Your ClassKendra code is {code}\n\n" +
               $"Enter it in the app to make {to} the backup email of the account with mobile number +91 {Mask.Phone(user.Phone)}. " +
               "It works for 10 minutes.\n\n" +
               "If your mobile number ever stops working, you can get your login code here instead.\n\n" +
               "Didn't ask for this? You can ignore this email.");

        if (!await email.SendAsync(to, subject, text, ct))
        {
            log.LogError("Email code for user {User} ({Purpose}) not delivered", user.Id, purpose);
            return EmailSendResult.Failed;
        }
        return EmailSendResult.Sent;
    }

    /// <summary>Checks the latest unused code. Returns the email it was sent to, or null if the code is wrong or expired.</summary>
    public async Task<string?> VerifyAsync(Guid userId, string purpose, string code, CancellationToken ct)
    {
        var row = await db.EmailCodes
            .Where(c => c.UserId == userId && c.Purpose == purpose && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (row is null || row.ExpiresAt < DateTime.UtcNow || row.Attempts >= MaxAttempts) return null;

        row.Attempts++;
        var ok = CryptographicOperations.FixedTimeEquals(row.CodeHash, Hash(userId, purpose, row.Email, code));
        if (ok) row.ConsumedAt = DateTime.UtcNow;   // a code works only once
        await db.SaveChangesAsync(ct);
        return ok ? row.Email : null;
    }

    /// <summary>A short note to an address (e.g. "your backup email was changed"). Failures are only logged.</summary>
    public async Task NotifyAsync(string to, string subject, string text, CancellationToken ct)
    {
        if (!await email.SendAsync(to, subject, text, ct))
            log.LogWarning("Notice email to {To} not delivered ({Subject})", to, subject);
    }

    /// <summary>"Amit.Sharma@Gmail.com " → "amit.sharma@gmail.com". Null if it isn't a usable address.</summary>
    public static string? Normalise(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length is < 6 or > 254) return null;
        if (!MailAddress.TryCreate(s, out var addr) || addr.Address != s) return null;
        var domain = s[(s.IndexOf('@') + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? s : null;
    }

    private byte[] Hash(Guid userId, string purpose, string to, string code) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(config["Otp:Secret"]!),
                            Encoding.UTF8.GetBytes($"email:{userId}:{purpose}:{to}:{code}"));
}

public static class Mask
{
    /// <summary>"amit.sharma@gmail.com" → "a••••••••••@gmail.com" (shown on the login screen, so the owner knows where to look).</summary>
    public static string Email(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "•••";
        var name = email[..at];
        return name[0] + new string('•', Math.Clamp(name.Length - 1, 3, 10)) + email[at..];
    }

    /// <summary>"9876543210" → "98•••••210".</summary>
    public static string Phone(string phone) =>
        phone.Length == 10 ? $"{phone[..2]}•••••{phone[^3..]}" : phone;
}
