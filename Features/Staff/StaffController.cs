using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Staff;

// Matches StaffMember in models.ts.
public record StaffDto(string Id, string Name, string Phone, string Role, string[] BatchIds);

/// <summary>Role: "Staff" (fees, students — everything except settings) or "Teacher" (attendance for their batches).</summary>
public record StaffInput(string Name, string Phone, string Role, string[]? BatchIds);

/// <summary>
/// People who can log in to the centre. They sign in with OTP on their own phone number.
/// One phone number = one ClassKendra account.
/// </summary>
[ApiController]
[Route("api/v1/staff")]
[Authorize(Roles = "Owner")]
public class StaffController(ClassKendraContext db, CurrentUser current, MessageService messages,
                             ILogger<StaffController> log) : ControllerBase
{
    private static readonly string[] Roles = ["Staff", "Teacher"];

    /// <summary>GET /staff — office staff and teachers (not the owner).</summary>
    [HttpGet]
    public async Task<ActionResult<List<StaffDto>>> List(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Include(u => u.Batches)
            .Where(u => u.IsActive && u.Role != "Owner")
            .OrderBy(u => u.Name)
            .ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    /// <summary>POST /staff — and a WhatsApp invite with the app link.</summary>
    [HttpPost]
    public async Task<ActionResult<StaffDto>> Create(StaffInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var (phone, batches, error) = await Validate(input, ct);
        if (error is not null) return BadRequest(new { message = error });

        // The number may already have signed up on its own (a "new owner" without a centre) — take that account over.
        var existing = await db.Users.IgnoreQueryFilters().Include(u => u.Batches)
            .FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);
        if (existing is not null && existing.TenantId is not null)
            return BadRequest(new { message = existing.TenantId == tenantId
                ? "Someone with this number is already added."
                : "This number is already used by another centre on ClassKendra." });

        var user = existing ?? new User { Phone = phone!, IsActive = true, CreatedAt = DateTime.UtcNow };
        if (existing is null) db.Users.Add(user);

        user.TenantId = tenantId;
        user.Name = input.Name.Trim();
        user.Role = input.Role;
        user.Batches.Clear();
        foreach (var b in batches!) user.Batches.Add(b);

        await db.SaveChangesAsync(ct);

        try { await messages.QueueStaffInviteAsync(user.Name, user.Phone, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Staff invite not queued for {User}", user.Id); }
        return ToDto(user);
    }

    /// <summary>PUT /staff/{id}</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<StaffDto>> Update(Guid id, StaffInput input, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Batches).FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
        if (user is null) return NotFound(new { message = "This person was removed." });
        if (user.Role == "Owner") return BadRequest(new { message = "Edit the owner from centre settings." });

        var (phone, batches, error) = await Validate(input, ct);
        if (error is not null) return BadRequest(new { message = error });
        if (phone != user.Phone &&
            await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Phone == phone && u.IsActive && u.Id != id, ct))
            return BadRequest(new { message = "This number is already used on ClassKendra." });

        user.Name = input.Name.Trim();
        user.Phone = phone!;
        user.Role = input.Role;
        user.Batches.Clear();
        foreach (var b in batches!) user.Batches.Add(b);

        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    /// <summary>DELETE /staff/{id} — they are signed out on their next request.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Batches).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null || !user.IsActive) return NoContent();
        if (user.Role == "Owner" || user.Id == current.UserId)
            return BadRequest(new { message = "The owner can't be removed." });

        user.IsActive = false;
        user.Batches.Clear();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- helpers ----------

    private async Task<(string? Phone, List<Batch>? Batches, string? Error)> Validate(StaffInput i, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return (null, null, "Enter a name.");
        if (Phone.Normalise(i.Phone) is not { } phone) return (null, null, "Enter a valid 10-digit mobile number.");
        if (!Roles.Contains(i.Role)) return (null, null, "Choose Staff or Teacher.");

        var ids = new List<Guid>();
        foreach (var raw in i.BatchIds ?? [])
        {
            if (!Guid.TryParse(raw, out var g)) return (null, null, "One of the batches was removed. Reload and try again.");
            ids.Add(g);
        }
        ids = ids.Distinct().ToList();
        List<Batch> batches = ids.Count == 0 ? [] : await db.Batches.Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        if (batches.Count != ids.Count) return (null, null, "One of the batches was removed. Reload and try again.");
        if (i.Role == "Teacher" && batches.Count == 0) return (null, null, "Choose the batches this teacher takes.");

        return (phone, batches, null);
    }

    private static StaffDto ToDto(User u) => new(
        u.Id.ToString(), u.Name, u.Phone, u.Role,
        u.Role == "Teacher" ? u.Batches.Select(b => b.Id.ToString()).ToArray() : []);
}
