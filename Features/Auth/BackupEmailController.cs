using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

// ---- Shapes that match models.ts ----
public record BackupEmailRequest(string Email, string? Pin);
public record EmailCodeRequest(string Code);
public record EmailLoginRequest(string Phone);
public record EmailLoginVerifyRequest(string Phone, string Code);
/// <summary>Where the code went, partly hidden on the login screen ("a•••@gmail.com").</summary>
public record EmailSentResponse(string SentTo);

/// <summary>
/// Backup email: a second way to get a login code when the mobile number stops working.
///  1. Signed in, the user enters an email (+ their PIN if they have one) → a code is sent to that email.
///  2. They type the code → the email is saved as verified. The previous backup email (if any) is told.
///  3. On the OTP screen, "Can't get the code?" → a login code goes to the backup email instead.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[SkipPlanCheck]
public class BackupEmailController(ClassKendraContext db, EmailCodeService codes, TokenService tokens,
                                   CurrentUser current, PinService pins) : ControllerBase
{
    /// <summary>
    /// POST /auth/email { email, pin? } -> { sentTo }
    /// Sends a code to the new email. Users with an app PIN must enter it, so someone holding an
    /// unlocked phone can't quietly add their own email and take the account later.
    /// </summary>
    [HttpPost("email")]
    [Authorize]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<EmailSentResponse>> Start(BackupEmailRequest request, CancellationToken ct)
    {
        var email = EmailCodeService.Normalise(request.Email);
        if (email is null) return BadRequest(new { message = "Enter a valid email address, e.g. name@gmail.com." });

        var user = await LoadUserAsync(current.UserId, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });

        if (user.PinHash is not null)
        {
            if (user.PinFailedAttempts >= PinService.MaxAttempts)
                return StatusCode(423, new { message = "Your PIN is locked after 5 wrong tries. Log in with OTP to unlock it." });
            if (!PinService.Matches(request.Pin ?? "", user.PinHash))
            {
                user.PinFailedAttempts++;
                await db.SaveChangesAsync(ct);
                var left = PinService.MaxAttempts - user.PinFailedAttempts;
                return BadRequest(new { message = left <= 0 ? "Your PIN is now locked. Log in with OTP to unlock it." : $"Wrong PIN. {left} tries left." });
            }
            user.PinFailedAttempts = 0;
        }

        if (email == user.Email) return BadRequest(new { message = "This is already your backup email." });
        if (await EmailTakenAsync(email, user.Id, ct))
            return Conflict(new { message = "This email is already the backup email of another account. Use a different one." });

        return await codes.SendAsync(user, email, EmailPurpose.Verify, ct) switch
        {
            EmailSendResult.Sent => new EmailSentResponse(email),
            EmailSendResult.TooManyRequests => StatusCode(429, new { message = "Too many codes requested. Please wait 10 minutes and try again." }),
            _ => StatusCode(502, new { message = "We couldn't send the email. Check the address and try again." }),
        };
    }

    /// <summary>POST /auth/email/verify { code } -> user — saves the email once the code is right.</summary>
    [HttpPost("email/verify")]
    [Authorize]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthUserDto>> Verify(EmailCodeRequest request, CancellationToken ct)
    {
        var user = await LoadUserAsync(current.UserId, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });

        var email = await codes.VerifyAsync(user.Id, EmailPurpose.Verify, (request.Code ?? "").Trim(), ct);
        if (email is null) return BadRequest(new { message = "That code is not right or has expired." });
        if (await EmailTakenAsync(email, user.Id, ct))
            return Conflict(new { message = "This email is already the backup email of another account. Use a different one." });

        var previous = user.Email;
        user.Email = email;
        user.EmailVerifiedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)   // UX_Users_Email: someone else saved it a moment ago
        {
            return Conflict(new { message = "This email is already the backup email of another account. Use a different one." });
        }

        if (previous is not null && previous != email)
            await codes.NotifyAsync(previous, "Your ClassKendra backup email was changed",
                $"The backup email of the ClassKendra account with mobile number +91 {Mask.Phone(user.Phone)} " +
                $"was changed from this address to {Mask.Email(email)} on {Clock.Today:d MMM yyyy}.\n\n" +
                "If you didn't do this, contact ClassKendra support straight away.", ct);

        return AuthController.ToDto(user);
    }

    /// <summary>DELETE /auth/email -> user — no backup email any more. The removed address is told.</summary>
    [HttpDelete("email")]
    [Authorize]
    public async Task<ActionResult<AuthUserDto>> Remove(CancellationToken ct)
    {
        var user = await LoadUserAsync(current.UserId, ct);
        if (user is null) return Unauthorized(new { message = "Please sign in again." });
        if (user.Email is null) return AuthController.ToDto(user);

        var removed = user.Email;
        user.Email = null;
        user.EmailVerifiedAt = null;
        await db.SaveChangesAsync(ct);

        await codes.NotifyAsync(removed, "Your ClassKendra backup email was removed",
            $"This address is no longer the backup email of the ClassKendra account with mobile number +91 {Mask.Phone(user.Phone)} " +
            $"(removed on {Clock.Today:d MMM yyyy}).\n\nIf you didn't do this, contact ClassKendra support.", ct);
        return AuthController.ToDto(user);
    }

    // ---------------- Login when the phone doesn't work ----------------

    /// <summary>
    /// POST /auth/email-login { phone } -> { sentTo }
    /// Sends a login code to the backup email of this mobile number. 404 if none is set.
    /// </summary>
    [HttpPost("email-login")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<EmailSentResponse>> StartLogin(EmailLoginRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalise(request.Phone);
        if (phone is null) return BadRequest(new { message = "Enter a valid 10-digit mobile number." });

        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);
        if (user?.Email is null)
            return NotFound(new { message = "No backup email is set for this number. Ask your centre owner or ClassKendra support for help." });

        return await codes.SendAsync(user, user.Email, EmailPurpose.Login, ct) switch
        {
            EmailSendResult.Sent => new EmailSentResponse(Mask.Email(user.Email)),
            EmailSendResult.TooManyRequests => StatusCode(429, new { message = "Too many codes requested. Please wait 10 minutes and try again." }),
            _ => StatusCode(502, new { message = "We couldn't send the email. Please try again." }),
        };
    }

    /// <summary>POST /auth/email-login/verify { phone, code } -> { token, user, deviceToken } — same as a phone OTP login.</summary>
    [HttpPost("email-login/verify")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> VerifyLogin(EmailLoginVerifyRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalise(request.Phone);
        if (phone is null || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { message = "Enter the 6-digit code." });

        var user = await db.Users.IgnoreQueryFilters()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);
        // Same answer for "no such user" and "wrong code", so the screen gives nothing away.
        if (user?.Email is null || await codes.VerifyAsync(user.Id, EmailPurpose.Login, request.Code.Trim(), ct) is null)
            return BadRequest(new { message = "That code is not right or has expired." });

        await pins.PruneAsync(user.Id, ct);
        user.LastLoginAt = DateTime.UtcNow;
        user.PinFailedAttempts = 0;   // like a phone OTP, this also unlocks a locked PIN
        var deviceKey = pins.AddDevice(user, Request.Headers.UserAgent);
        await db.SaveChangesAsync(ct);

        return new AuthResponse(tokens.Create(user), AuthController.ToDto(user), deviceKey);
    }

    // ---------------- Helpers ----------------

    private Task<User?> LoadUserAsync(Guid id, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);

    private Task<bool> EmailTakenAsync(string email, Guid exceptUserId, CancellationToken ct) =>
        db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != exceptUserId, ct);
}
