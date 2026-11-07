using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

// ---- Shapes that match models.ts exactly ----
public record OtpRequest(string Phone);
public record VerifyRequest(string Phone, string Code);
public record FirebaseLoginRequest(string IdToken);
public record SetPinRequest(string Pin, string? DeviceToken);
public record UnlockRequest(string DeviceToken, string Pin);
public record DeviceRequest(string DeviceToken);

/// <summary>HasPin: an app PIN is set. PinRequired: owner and office staff must set one (teachers may skip).</summary>
public record AuthUserDto(string Id, string Phone, string Name, string Role, string? TenantId, string[] BatchIds,
    bool HasPin, bool PinRequired);

/// <summary>DeviceToken is only sent when the app should store a new one; null = keep the one you have.</summary>
public record AuthResponse(string Token, AuthUserDto User, string? DeviceToken);

/// <summary>
/// Login.
///  1. OTP (WhatsApp) or Firebase SMS  → token + a device key the app keeps (this phone is remembered for 30 days).
///  2. The app asks the user to set a 4-digit PIN (required for owner and staff).
///  3. After 10 minutes away the app drops its token and shows the PIN screen → /auth/unlock with device key + PIN.
///  4. 5 wrong PINs lock the PIN; the user logs in with OTP again (which also unlocks it).
///  5. Users without a PIN are renewed silently with /auth/refresh, so regular users never see OTP again.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[SkipPlanCheck]
public class AuthController(ClassKendraContext db, OtpService otp, TokenService tokens, CurrentUser current,
                            FirebaseTokenValidator firebase, PinService pins) : ControllerBase
{
    /// <summary>POST /auth/otp { phone } — sends a 6-digit code.</summary>
    [HttpPost("otp")]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestOtp(OtpRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalise(request.Phone);
        if (phone is null)
            return BadRequest(new { message = "Enter a valid 10-digit mobile number." });

        var result = await otp.SendAsync(phone, ct);
        if (result == OtpSendResult.TooManyRequests)
            return StatusCode(429, new { message = "Too many codes requested. Please wait a few minutes and try again." });
        if (result == OtpSendResult.Failed)
            return StatusCode(502, new { message = "We couldn't send the code on WhatsApp. Please try again." });

        return NoContent();
    }

    /// <summary>POST /auth/verify { phone, code } -> { token, user, deviceToken }</summary>
    [HttpPost("verify")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> Verify(VerifyRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalise(request.Phone);
        if (phone is null || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { message = "Enter the 6-digit code." });

        if (!await otp.VerifyAsync(phone, request.Code.Trim(), ct))
            return BadRequest(new { message = "That code is not right or has expired." });

        return await SignInAsync(phone, ct);
    }

    /// <summary>
    /// POST /auth/firebase { idToken } -> { token, user, deviceToken }
    /// The app sent and checked the SMS code with Firebase; this checks Firebase's proof and signs the user in.
    /// </summary>
    [HttpPost("firebase")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> Firebase(FirebaseLoginRequest request, CancellationToken ct)
    {
        if (!firebase.Enabled)
            return NotFound(new { message = "SMS login is not set up on this server." });

        var phone = Phone.Normalise(await firebase.VerifiedPhoneAsync(request.IdToken, ct));
        if (phone is null)
            return BadRequest(new { message = "We couldn't confirm your number. Please request a new code." });

        return await SignInAsync(phone, ct);
    }

    /// <summary>GET /auth/me — the signed-in user again (role, centre, batches and PIN may have changed).</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthUserDto>> Me(CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Id == current.UserId && u.IsActive, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });
        return ToDto(user);
    }

    // ---------------- App PIN ----------------

    /// <summary>
    /// POST /auth/pin { pin, deviceToken? } -> { token, user, deviceToken }
    /// Sets or changes the PIN for the signed-in user. If this phone isn't remembered yet, it is now
    /// (the PIN only works on remembered devices).
    /// </summary>
    [HttpPost("pin")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> SetPin(SetPinRequest request, CancellationToken ct)
    {
        if (PinService.CheckNewPin(request.Pin) is string problem)
            return BadRequest(new { message = problem });

        var user = await LoadUserAsync(current.UserId, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });

        user.PinHash = PinService.Hash(request.Pin);
        user.PinFailedAttempts = 0;
        user.PinSetAt = DateTime.UtcNow;

        var device = await pins.FindDeviceAsync(request.DeviceToken, ct);
        string? newKey = null;
        if (device is null || device.UserId != user.Id) newKey = pins.AddDevice(user, Request.Headers.UserAgent);
        else PinService.Touch(device);

        await db.SaveChangesAsync(ct);
        return new AuthResponse(tokens.Create(user), ToDto(user), newKey);
    }

    /// <summary>DELETE /auth/pin — teachers can turn their PIN off. Owner and staff must keep one.</summary>
    [HttpDelete("pin")]
    [Authorize]
    public async Task<ActionResult<AuthUserDto>> RemovePin(CancellationToken ct)
    {
        var user = await LoadUserAsync(current.UserId, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });
        if (PinRequired(user)) return BadRequest(new { message = "Owners and office staff need a PIN because the app shows fees." });

        user.PinHash = null;
        user.PinFailedAttempts = 0;
        user.PinSetAt = null;
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    /// <summary>
    /// POST /auth/unlock { deviceToken, pin } -> { token, user }
    /// 400 wrong PIN (with attemptsLeft) · 423 PIN locked after 5 tries (log in with OTP) · 401 device not remembered.
    /// </summary>
    [HttpPost("unlock")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> Unlock(UnlockRequest request, CancellationToken ct)
    {
        var device = await pins.FindDeviceAsync(request.DeviceToken, ct);
        if (device is null)
            return Unauthorized(new { message = "Please log in with your mobile number again." });

        var user = device.User;
        if (user.PinHash is null)
            return Conflict(new { message = "You haven't set a PIN yet.", noPin = true });
        if (user.PinFailedAttempts >= PinService.MaxAttempts)
            return StatusCode(423, new { message = "Your PIN is locked after 5 wrong tries. Log in with OTP to unlock it.", locked = true });

        if (!PinService.Matches(request.Pin ?? "", user.PinHash))
        {
            user.PinFailedAttempts++;
            await db.SaveChangesAsync(ct);
            var left = PinService.MaxAttempts - user.PinFailedAttempts;
            return left <= 0
                ? StatusCode(423, new { message = "Your PIN is locked after 5 wrong tries. Log in with OTP to unlock it.", locked = true })
                : BadRequest(new { message = left == 1 ? "Wrong PIN. 1 try left." : $"Wrong PIN. {left} tries left.", attemptsLeft = left });
        }

        user.PinFailedAttempts = 0;
        user.LastLoginAt = DateTime.UtcNow;
        PinService.Touch(device);
        await db.SaveChangesAsync(ct);
        return new AuthResponse(tokens.Create(user), ToDto(user), null);
    }

    /// <summary>
    /// POST /auth/refresh { deviceToken } -> { token, user }
    /// Renews the login for users without a PIN, so they stay logged in while they keep using the app.
    /// Users with a PIN get 409 { pinRequired } and renew by unlocking instead.
    /// </summary>
    [HttpPost("refresh")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> Refresh(DeviceRequest request, CancellationToken ct)
    {
        var device = await pins.FindDeviceAsync(request.DeviceToken, ct);
        if (device is null)
            return Unauthorized(new { message = "Please log in with your mobile number again." });
        if (device.User.PinHash is not null)
            return Conflict(new { message = "Enter your PIN.", pinRequired = true });

        device.User.LastLoginAt = DateTime.UtcNow;
        PinService.Touch(device);
        await db.SaveChangesAsync(ct);
        return new AuthResponse(tokens.Create(device.User), ToDto(device.User), null);
    }

    /// <summary>POST /auth/logout { deviceToken } — forgets this phone. Always 204.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(DeviceRequest request, CancellationToken ct)
    {
        await pins.RemoveDeviceAsync(request.DeviceToken, ct);
        return NoContent();
    }

    // ---------------- Helpers ----------------

    /// <summary>Finds (or creates) the user for a verified phone number, remembers this device and returns our token.</summary>
    private async Task<ActionResult<AuthResponse>> SignInAsync(string phone, CancellationToken ct)
    {
        // Staff/teachers were added by the owner; anyone else becomes a new owner (no centre yet).
        var user = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);

        if (user is null)
        {
            user = new User { Id = Guid.NewGuid(), Phone = phone, Name = "", Role = "Owner", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Users.Add(user);
        }
        else
        {
            await pins.PruneAsync(user.Id, ct);
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.PinFailedAttempts = 0;   // a verified OTP also unlocks a locked PIN
        var deviceKey = pins.AddDevice(user, Request.Headers.UserAgent);
        await db.SaveChangesAsync(ct);

        return new AuthResponse(tokens.Create(user), ToDto(user), deviceKey);
    }

    private Task<User?> LoadUserAsync(Guid id, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);

    private static bool PinRequired(User user) => user.Role is "Owner" or "Staff";

    private static AuthUserDto ToDto(User user) => new(
        user.Id.ToString(),
        user.Phone,
        user.Name,
        user.Role,
        user.TenantId?.ToString(),
        user.Role == "Teacher" ? user.Batches.Select(b => b.Id.ToString()).ToArray() : [],
        user.PinHash is not null,
        PinRequired(user));
}
