using System.Security.Cryptography;
using System.Text;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

public enum OtpSendResult { Sent, TooManyRequests, Failed }

public class OtpService(ClassKendraContext db, IConfiguration config,
                        IWhatsAppSender whatsApp, ILogger<OtpService> log)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;          // wrong guesses allowed per code
    private const int MaxSendsPer10Min = 3;     // stops OTP spamming

    public async Task<OtpSendResult> SendAsync(string phone, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var recent = await db.OtpCodes.CountAsync(o => o.Phone == phone && o.CreatedAt >= now.AddMinutes(-10), ct);
        if (recent >= MaxSendsPer10Min) return OtpSendResult.TooManyRequests;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        db.OtpCodes.Add(new OtpCode
        {
            Phone = phone,
            CodeHash = Hash(phone, code),       // the code itself is never stored
            ExpiresAt = now.Add(Lifetime),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        // Development uses LogWhatsAppSender: the code shows in the Output window.
        var sent = await whatsApp.SendOtpAsync(phone, code, ct);
        if (!sent.Ok)
        {
            log.LogError("OTP to {Phone} not delivered: {Error}", phone, sent.Error);
            return OtpSendResult.Failed;
        }
        return OtpSendResult.Sent;
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