using System.Text.Json;
using System.Text.RegularExpressions;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Setup;

// ---- Shapes that match models.ts (Centre, CentreInput) ----
public record CentreDto(
    string Id, string Name, string OwnerName, string City, string InstitutionType,
    string[] Subjects, string SizeBand, string Plan, string TrialEndsOn,
    int MessagesUsed, int MessageLimit, bool SetupComplete, string MessageLanguage,
    string Address, string Phone, string? LogoId);

/// <param name="Address">Optional. Null = leave as it is (older app versions don't send it); "" = clear.</param>
/// <param name="Phone">Optional, same rule as Address.</param>
public record CentreInput(
    string Name, string OwnerName, string City, string[]? Subjects,
    string? SizeBand, string InstitutionType, string? Address = null, string? Phone = null);

public record LanguageInput(string Language);

[ApiController]
[Route("api/v1/centre")]
[Authorize]
[SkipPlanCheck]
public class CentreController(ClassKendraContext db, CurrentUser current) : ControllerBase
{
    private const int TrialDays = 30;
    private const int TrialMessageLimit = 500;

    /// <summary>GET /centre — null until the owner creates one.</summary>
    [HttpGet]
    public async Task<ActionResult<CentreDto?>> Get(CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);   // filter = only my centre
        return tenant is null ? Ok(null) : ToDto(tenant);
    }

    /// <summary>POST /centre — setup step 1 (first time only).</summary>
    [HttpPost]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CentreDto>> Create(CentreInput input, CancellationToken ct)
    {
        if (current.TenantId is not null)
            return Conflict(new { message = "Your centre is already set up." });
        if (Validate(input) is { } error)
            return BadRequest(new { message = error });

        var tenant = new Tenant
        {
            PlanCode = "Trial",
            TrialEndsOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(TrialDays)),
            MessageLimit = TrialMessageLimit,
            MessageLanguage = "en",
            CreatedAt = DateTime.UtcNow
        };
        Apply(tenant, input);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);                // gives tenant.Id

        // Link the owner to the new centre, and create default fee settings.
        var owner = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == current.UserId, ct);
        owner.TenantId = tenant.Id;
        owner.Name = input.OwnerName.Trim();
        db.FeeSettings.Add(new FeeSetting { TenantId = tenant.Id });
        await db.SaveChangesAsync(ct);

        return ToDto(tenant);
    }

    /// <summary>PUT /centre — edit centre details.</summary>
    [HttpPut]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CentreDto>> Update(CentreInput input, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Set up your centre first." });
        if (Validate(input) is { } error) return BadRequest(new { message = error });

        Apply(tenant, input);
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    /// <summary>PUT /centre/setup-complete</summary>
    [HttpPut("setup-complete")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CentreDto>> CompleteSetup(CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Set up your centre first." });

        tenant.SetupComplete = true;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    /// <summary>PUT /centre/message-language { language }</summary>
    [HttpPut("message-language")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CentreDto>> SetLanguage(LanguageInput input, CancellationToken ct)
    {
        if (input.Language is not ("en" or "hi"))
            return BadRequest(new { message = "Choose English or Hindi." });

        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Set up your centre first." });

        tenant.MessageLanguage = input.Language;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    // ---------- Logo (printed on receipts) ----------

    private const int MaxLogoBytes = 512 * 1024;

    /// <summary>
    /// POST /centre/logo (multipart, field "file") — PNG, JPEG or WebP up to 512 KB.
    /// Adds a new logo row and makes it the current one; old rows stay for old receipts.
    /// </summary>
    [HttpPost("logo")]
    [Authorize(Roles = "Owner")]
    [RequestSizeLimit(MaxLogoBytes + 64 * 1024)]
    public async Task<ActionResult<CentreDto>> UploadLogo(IFormFile? file, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Set up your centre first." });
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose an image for your logo." });
        if (file.Length > MaxLogoBytes) return BadRequest(new { message = "The logo must be under 512 KB." });

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var data = buffer.ToArray();
        var type = ImageType(data);   // trust the bytes, not the file name
        if (type is null) return BadRequest(new { message = "Use a PNG, JPG or WebP image." });

        var logo = new CentreLogo
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContentType = type,
            Data = data,
            CreatedAt = DateTime.UtcNow
        };
        db.CentreLogos.Add(logo);
        tenant.LogoId = logo.Id;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    /// <summary>DELETE /centre/logo — stop printing a logo on new receipts (old receipts keep theirs).</summary>
    [HttpDelete("logo")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CentreDto>> RemoveLogo(CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
        if (tenant is null) return NotFound(new { message = "Set up your centre first." });

        tenant.LogoId = null;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(tenant);
    }

    /// <summary>
    /// GET /centre/logo/{id} — the image itself. No login, because an &lt;img&gt; tag can't send one;
    /// the id is random, and a logo never changes, so browsers may cache it for good.
    /// </summary>
    [HttpGet("logo/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Logo(Guid id, CancellationToken ct)
    {
        var logo = await db.CentreLogos.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new { l.ContentType, l.Data })
            .FirstOrDefaultAsync(ct);
        if (logo is null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(logo.Data, logo.ContentType);
    }

    private static string? ImageType(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' &&
            d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
        return null;
    }

    // ---------- helpers ----------

    private static string? Validate(CentreInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Enter your centre's name.";
        if (string.IsNullOrWhiteSpace(i.OwnerName)) return "Enter the owner's name.";
        if (string.IsNullOrWhiteSpace(i.City)) return "Enter your city.";
        if (i.InstitutionType is not ("Coaching" or "School")) return "Choose coaching centre or school.";
        if (i.Address is { Length: > 300 }) return "Keep the address under 300 characters.";
        if (!string.IsNullOrWhiteSpace(i.Phone) && !PhonePattern().IsMatch(i.Phone.Trim()))
            return "Enter a valid phone number, e.g. 98765 43210 or 0522-2345678.";
        return null;
    }

    /// <summary>Mobile or landline as people type them: digits, spaces, +, - and brackets.</summary>
    private static readonly Regex PhoneRegex = new(@"^\+?[0-9 ()\-]{6,20}$", RegexOptions.Compiled);
    private static Regex PhonePattern() => PhoneRegex;

    private static void Apply(Tenant t, CentreInput i)
    {
        t.Name = i.Name.Trim();
        t.OwnerName = i.OwnerName.Trim();
        t.City = i.City.Trim();
        t.InstitutionType = i.InstitutionType;
        t.Subjects = JsonSerializer.Serialize(i.Subjects ?? []);
        t.SizeBand = i.SizeBand?.Trim();
        if (i.Address is not null) t.Address = NullIfEmpty(i.Address);
        if (i.Phone is not null) t.Phone = NullIfEmpty(i.Phone);
        t.UpdatedAt = DateTime.UtcNow;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static CentreDto ToDto(Tenant t) => new(
        t.Id.ToString(), t.Name, t.OwnerName, t.City, t.InstitutionType,
        ReadSubjects(t.Subjects), t.SizeBand ?? "", t.PlanCode,
        t.TrialEndsOn.ToString("yyyy-MM-dd"),
        t.MessagesUsed, t.MessageLimit, t.SetupComplete, t.MessageLanguage,
        t.Address ?? "", t.Phone ?? "", t.LogoId?.ToString());

    private static string[] ReadSubjects(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }
}