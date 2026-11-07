using System.Security.Cryptography;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

/// <summary>
/// App PIN and remembered devices.
/// - The PIN is stored as a salted PBKDF2 hash; 5 wrong tries lock it until the next OTP login.
/// - A device key is a random secret the app keeps after an OTP login; we store only its SHA-256 hash.
///   PIN unlock and silent renewal work only with a known device key, so a new phone always needs OTP.
/// </summary>
public class PinService(ClassKendraContext db)
{
    public const int MaxAttempts = 5;
    public const int DeviceDays = 30;
    private const int Iterations = 100_000;

    // ---------------- PIN ----------------

    /// <summary>Null if the PIN is fine, otherwise a sentence for the user.</summary>
    public static string? CheckNewPin(string? pin)
    {
        if (pin is null || pin.Length != 4 || !pin.All(char.IsAsciiDigit)) return "Enter 4 digits.";
        if (pin.Distinct().Count() == 1) return "Choose a PIN that isn't the same digit 4 times.";
        if ("0123456789".Contains(pin) || "9876543210".Contains(pin)) return "Choose a PIN that isn't 1234 or 4321 style.";
        return null;
    }

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Matches(string pin, string? stored)
    {
        var parts = stored?.Split('.');
        if (parts is not { Length: 4 } || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ---------------- Devices ----------------

    /// <summary>Remembers this phone/browser for 30 days and returns the key the app must keep.</summary>
    public string AddDevice(User user, string? userAgent)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTime.UtcNow;
        db.UserDevices.Add(new UserDevice
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            KeyHash = KeyHash(key),
            UserAgent = (userAgent ?? "").Length > 200 ? userAgent![..200] : userAgent ?? "",
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.AddDays(DeviceDays),
        });
        return key;
    }

    /// <summary>The device for this key (with its active user), or null if unknown, expired or the user was removed.</summary>
    public async Task<UserDevice?> FindDeviceAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) return null;
        var hash = KeyHash(key);
        var device = await db.UserDevices
            .Include(d => d.User).ThenInclude(u => u.Batches)
            .IgnoreQueryFilters()   // no centre is known before login
            .FirstOrDefaultAsync(d => d.KeyHash == hash, ct);
        if (device is null || device.ExpiresAt < DateTime.UtcNow || !device.User.IsActive) return null;
        return device;
    }

    /// <summary>Pushes the 30 days forward: anyone who keeps using the app stays logged in.</summary>
    public static void Touch(UserDevice device)
    {
        device.LastUsedAt = DateTime.UtcNow;
        device.ExpiresAt = device.LastUsedAt.AddDays(DeviceDays);
    }

    public async Task RemoveDeviceAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) return;
        var hash = KeyHash(key);
        await db.UserDevices.IgnoreQueryFilters().Where(d => d.KeyHash == hash).ExecuteDeleteAsync(ct);
    }

    /// <summary>Clears out expired devices for a user (keeps the table small).</summary>
    public Task<int> PruneAsync(Guid userId, CancellationToken ct) =>
        db.UserDevices.IgnoreQueryFilters()
            .Where(d => d.UserId == userId && d.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync(ct);

    private static byte[] KeyHash(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
}
