using System.Security.Cryptography;
using System.Text;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

public enum OtpSendResult { Sent, TooManyRequests, Failed }

/// <summary>ScreenCode is only set in demo mode (Otp:ShowOnScreen = true): the app shows it instead of sending it.</summary>
public record OtpSendOutcome(OtpSendResult Result, string? ScreenCode = null);

public class OtpService(ClassKendraContext db, IConfiguration config,
                        IWhatsAppSender whatsApp, ILogger<OtpService> log)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;          // wrong guesses allowed per code
    private const int MaxSendsPer10Min = 3;     // stops OTP spamming

    /// <summary>
    /// Demo mode: Otp:ShowOnScreen = true. The code is still saved (hashed) in the database and checked
    /// exactly the same way, but it is returned to the app to show on screen instead of being sent on WhatsApp.
    /// Anyone can then sign in to any number, so switch it off before real customers use the app.
    /// </summary>
    public bool ShowOnScreen => config.GetValue<bool>("Otp:ShowOnScreen");

    public async Task<OtpSendOutcome> SendAsync(string phone, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var recent = await db.OtpCodes.CountAsync(o => o.Phone == phone && o.CreatedAt >= now.AddMinutes(-10), ct);
        if (recent >= MaxSendsPer10Min) return new(OtpSendResult.TooManyRequests);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        db.OtpCodes.Add(new OtpCode
        {
            Phone = phone,
            CodeHash = Hash(phone, code),       // the code itself is never stored
            ExpiresAt = now.Add(Lifetime),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        if (ShowOnScreen)
        {
            log.LogInformation("Demo OTP for {Phone} shown on screen", phone);
            return new(OtpSendResult.Sent, code);
        }

        // Development uses LogWhatsAppSender: the code shows in the Output window.
        var sent = await whatsApp.SendOtpAsync(phone, code, ct);
        if (!sent.Ok)
        {
            log.LogError("OTP to {Phone} not delivered: {Error}", phone, sent.Error);
            return new(OtpSendResult.Failed);
        }
        return new(OtpSendResult.Sent);
    }

    public async Task<bool> VerifyAsync(string phone, string code, CancellationToken ct)
    {
        var otp = await db.OtpCodes
            .Where(o => o.Phone == phone && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (otp is null || otp.ExpiresAt < DateTime.UtcNow || otp.Attempts >= MaxAttempts)
            return false;

        otp.Attempts++;
        var ok = CryptographicOperations.FixedTimeEquals(otp.CodeHash, Hash(phone, code));
        if (ok) otp.ConsumedAt = DateTime.UtcNow;     // a code works only once

        await db.SaveChangesAsync(ct);
        return ok;
    }

    private byte[] Hash(string phone, string code) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(config["Otp:Secret"]!),
                            Encoding.UTF8.GetBytes($"{phone}:{code}"));
}