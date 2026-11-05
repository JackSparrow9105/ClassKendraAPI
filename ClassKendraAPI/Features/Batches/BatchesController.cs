using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Batches;

// ---- Shapes that match models.ts (Batch, BatchInput) ----
public record BatchDto(string Id, string Name, string? ClassName, string? Section,
                       string? Days, string? StartTime, string? EndTime);

public record BatchInput(string Name, string? ClassName, string? Section,
                         string? Days, string? StartTime, string? EndTime);

[ApiController]
[Route("api/v1/batches")]
[Authorize]
public class BatchesController(ClassKendraContext db, CurrentUser current) : ControllerBase
{
    private static readonly string[] ClassOrder = ["Nursery", "LKG", "UKG"];

    /// <summary>GET /batches — teachers only get their own classes.</summary>
    [HttpGet]
    public async Task<ActionResult<List<BatchDto>>> List(CancellationToken ct)
    {
        var query = db.Batches.AsNoTracking();
        if (current.Role == "Teacher")
            query = query.Where(b => b.Users.Any(u => u.Id == current.UserId));

        var rows = await query.ToListAsync(ct);
        return rows.OrderBy(SortKey).ThenBy(b => b.Section).ThenBy(b => b.Name)
                   .Select(ToDto).ToList();
    }

    /// <summary>POST /batches — schools call this once per section.</summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<BatchDto>> Create(BatchInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });
        if (await Validate(input, null, ct) is { } error)
            return BadRequest(new { message = error });

        var batch = new Batch { TenantId = tenantId, CreatedAt = DateTime.UtcNow };
        Apply(batch, input);
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);
        return ToDto(batch);
    }

    /// <summary>PUT /batches/{id}</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<BatchDto>> Update(Guid id, BatchInput input, CancellationToken ct)
    {
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { message = "That batch no longer exists." });
        if (await Validate(input, id, ct) is { } error)
            return BadRequest(new { message = error });

        Apply(batch, input);
        await db.SaveChangesAsync(ct);
        return ToDto(batch);
    }

    /// <summary>DELETE /batches/{id} — refused while students or records point at it.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NoContent();

        if (await db.Students.AnyAsync(s => s.BatchId == id && s.IsActive, ct))
            return BadRequest(new { message = "Move the students out first, then delete it." });

        var hasHistory =
            await db.Students.AnyAsync(s => s.BatchId == id, ct) ||
            await db.FeeDues.AnyAsync(d => d.BatchId == id, ct) ||
            await db.AttendanceSheets.AnyAsync(a => a.BatchId == id, ct);
        if (hasHistory)
            return BadRequest(new { message = "This batch has past fee or attendance records, so it can't be deleted. Rename it instead." });

        db.Batches.Remove(batch);   // fee amounts and teacher links are removed by the database
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- helpers ----------

    private async Task<string?> Validate(BatchInput i, Guid? editingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Enter a name.";

        var name = i.Name.Trim();
        if (await db.Batches.AnyAsync(b => b.Name == name && b.Id != editingId, ct))
            return $"{name} already exists.";

        if (!IsTimeOrEmpty(i.StartTime) || !IsTimeOrEmpty(i.EndTime))
            return "Enter times like 07:00.";
        if (TimeOnly.TryParse(i.StartTime, out var start) && TimeOnly.TryParse(i.EndTime, out var end) && end <= start)
            return "End time must be after start time.";

        return null;
    }

    private static bool IsTimeOrEmpty(string? t) => string.IsNullOrWhiteSpace(t) || TimeOnly.TryParse(t, out _);

    private static void Apply(Batch b, BatchInput i)
    {
        b.Name = i.Name.Trim();
        b.ClassName = Blank(i.ClassName);
        b.Section = Blank(i.Section);
        b.Days = Blank(i.Days);
        b.StartTime = TimeOnly.TryParse(i.StartTime, out var s) ? s : null;
        b.EndTime = TimeOnly.TryParse(i.EndTime, out var e) ? e : null;
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static BatchDto ToDto(Batch b) => new(
        b.Id.ToString(), b.Name, b.ClassName, b.Section, b.Days,
        b.StartTime?.ToString("HH:mm"), b.EndTime?.ToString("HH:mm"));

    /// <summary>Nursery, LKG, UKG, 1…12, then coaching batches by name.</summary>
    private static int SortKey(Batch b)
    {
        if (b.ClassName is null) return 1000;
        var i = Array.IndexOf(ClassOrder, b.ClassName);
        return i >= 0 ? i - 10 : int.TryParse(b.ClassName, out var n) ? n : 999;
    }
}