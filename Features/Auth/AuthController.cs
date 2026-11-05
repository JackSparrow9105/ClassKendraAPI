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
public record AuthUserDto(string Id, string Phone, string Name, string Role, string? TenantId, string[] BatchIds);
public record AuthResponse(string Token, AuthUserDto User);

[ApiController]
[Route("api/v1/auth")]
[SkipPlanCheck]
public class AuthController(ClassKendraContext db, OtpService otp, TokenService tokens, CurrentUser current) : ControllerBase
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

    /// <summary>POST /auth/verify { phone, code } -> { token, user }</summary>
    [HttpPost("verify")]
    [EnableRateLimiting("otp")]
    public async Task<ActionResult<AuthResponse>> Verify(VerifyRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalise(request.Phone);
        if (phone is null || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { message = "Enter the 6-digit code." });

        if (!await otp.VerifyAsync(phone, request.Code.Trim(), ct))
            return BadRequest(new { message = "That code is not right or has expired." });

        // Staff/teachers were added by the owner; anyone else becomes a new owner (no centre yet).
        var user = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);

        if (user is null)
        {
            user = new User { Phone = phone, Name = "", Role = "Owner", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Users.Add(user);
        }
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new AuthResponse(tokens.Create(user), ToDto(user));
    }

    /// <summary>GET /auth/me — the signed-in user again (role, centre and batches may have changed).</summary>
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

    private static AuthUserDto ToDto(User user) => new(
        user.Id.ToString(),
        user.Phone,
        user.Name,
        user.Role,
        user.TenantId?.ToString(),
        user.Role == "Teacher" ? user.Batches.Select(b => b.Id.ToString()).ToArray() : []);
}