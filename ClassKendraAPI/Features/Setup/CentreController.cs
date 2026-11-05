using System.Text.Json;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Setup;

// ---- Shapes that match models.ts (Centre, CentreInput) ----
public record CentreDto(
    string Id, string Name, string OwnerName, string City, string InstitutionType,
    string[] Subjects, string SizeBand, string Plan, string TrialEndsOn,
    int MessagesUsed, int MessageLimit, bool SetupComplete, string MessageLanguage);

public record CentreInput(
    string Name, string OwnerName, string City, string[]? Subjects,
    string? SizeBand, string InstitutionType);

public record LanguageInput(string Language);

[ApiController]
[Route("api/v1/centre")]
[Authorize]
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

    // ---------- helpers ----------

    private static string? Validate(CentreInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Enter your centre's name.";
        if (string.IsNullOrWhiteSpace(i.OwnerName)) return "Enter the owner's name.";
        if (string.IsNullOrWhiteSpace(i.City)) return "Enter your city.";
        if (i.InstitutionType is not ("Coaching" or "School")) return "Choose coaching centre or school.";
        return null;
    }

    private static void Apply(Tenant t, CentreInput i)
    {
        t.Name = i.Name.Trim();
        t.OwnerName = i.OwnerName.Trim();
        t.City = i.City.Trim();
        t.InstitutionType = i.InstitutionType;
        t.Subjects = JsonSerializer.Serialize(i.Subjects ?? []);
        t.SizeBand = i.SizeBand?.Trim();
        t.UpdatedAt = DateTime.UtcNow;
    }

    private static CentreDto ToDto(Tenant t) => new(
        t.Id.ToString(), t.Name, t.OwnerName, t.City, t.InstitutionType,
        ReadSubjects(t.Subjects), t.SizeBand ?? "", t.PlanCode,
        t.TrialEndsOn.ToString("yyyy-MM-dd"),
        t.MessagesUsed, t.MessageLimit, t.SetupComplete, t.MessageLanguage);

    private static string[] ReadSubjects(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }
}