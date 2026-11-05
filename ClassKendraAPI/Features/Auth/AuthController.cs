using System.Text.RegularExpressions;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Auth;

// ---- Shapes that match models.ts exactly ----
public record OtpRequest(string Phone);
public record VerifyRequest(string Phone, string Code);
public record AuthUserDto(string Id, string Phone, string Name, string Role, string? TenantId, string[] BatchIds);
public record AuthResponse(string Token, AuthUserDto User);

[ApiController]
[Route("api/v1/auth")]
public class AuthController(ClassKendraContext db, OtpService otp, TokenService tokens) : ControllerBase
{
    /// <summary>POST /auth/otp { phone } — sends a 6-digit code.</summary>
    [HttpPost("otp")]
    public async Task<IActionResult> RequestOtp(OtpRequest request, CancellationToken ct)
    {
        var phone = NormalisePhone(request.Phone);
        if (phone is null)
            return BadRequest(new { message = "Enter a valid 10-digit mobile number." });

        var result = await otp.SendAsync(phone, ct);
        if (result == OtpSendResult.TooManyRequests)
            return StatusCode(429, new { message = "Too many codes requested. Please wait a few minutes and try again." });

        return NoContent();
    }

    /// <summary>POST /auth/verify { phone, code } -> { token, user }</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<AuthResponse>> Verify(VerifyRequest request, CancellationToken ct)
    {
        var phone = NormalisePhone(request.Phone);
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

        var dto = new AuthUserDto(
            user.Id.ToString(),
            user.Phone,
            user.Name,
            user.Role,
            user.TenantId?.ToString(),
            user.Role == "Teacher" ? user.Batches.Select(b => b.Id.ToString()).ToArray() : []);

        return new AuthResponse(tokens.Create(user), dto);
    }

    /// <summary>Accepts "98765 43210", "+91 9876543210", "09876543210" → "9876543210".</summary>
    private static string? NormalisePhone(string? raw)
    {
        var digits = Regex.Replace(raw ?? "", @"\D", "");
        if (digits.Length == 12 && digits.StartsWith("91")) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        return Regex.IsMatch(digits, "^[6-9][0-9]{9}$") ? digits : null;
    }
}