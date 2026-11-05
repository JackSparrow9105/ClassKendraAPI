using System.Security.Cryptography;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

// ---- Shapes that match models.ts (Student, Concession, StudentInput) ----
public record ConcessionDto(string Id, string Reason, string Note, string? FeeHeadId, string Kind, int Value);

public record StudentDto(
    string Id, string Name, string ParentName, string ParentPhone, string BatchId,
    string JoinedOn, bool Active, string[] OptionalHeadIds, ConcessionDto[] Concessions);

public record StudentInput(
    string Name, string? ParentName, string ParentPhone, string BatchId,
    string? JoinedOn, string[]? OptionalHeadIds);

public record ConcessionInput(string Reason, string? Note, string? FeeHeadId, string Kind, int Value);

[ApiController]
[Route("api/v1/students")]
[Authorize]
public class StudentsController(ClassKendraContext db, CurrentUser current) : ControllerBase
{
    private static readonly string[] Reasons = ["Sibling", "Staff child", "Scholarship", "Other"];

    /// <summary>GET /students?batchId=&search= — active students only; teachers see their own classes.</summary>
    [HttpGet]
    public async Task<ActionResult<List<StudentDto>>> List([FromQuery] Guid? batchId, [FromQuery] string? search, CancellationToken ct)
    {
        var q = WithDetails().AsNoTracking().Where(s => s.IsActive);

        if (current.Role == "Teacher")
            q = q.Where(s => s.Batch.Users.Any(u => u.Id == current.UserId));
        if (batchId is Guid b)
            q = q.Where(s => s.BatchId == b);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(s => s.Name.Contains(term) || s.ParentPhone.Contains(term));
        }

        var rows = await q.OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>GET /students/{id}</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StudentDto>> Get(Guid id, CancellationToken ct)
    {
        var s = await WithDetails().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || (current.Role == "Teacher" && !await TeacherCanSee(s.BatchId, ct)))
            return NotFound(new { message = "Student not found." });
        return ToDto(s);
    }

    /// <summary>POST /students</summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> Create(StudentInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });

        var (clean, error) = await Validate(input, null, ct);
        if (error is not null) return BadRequest(new { message = error });

        var s = new Student
        {
            TenantId = tenantId,
            PayToken = NewPayToken(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        Apply(s, clean!);
        foreach (var head in await LoadOptionalHeads(clean!.OptionalHeadIds, ct)) s.FeeHeads.Add(head);

        db.Students.Add(s);
        await db.SaveChangesAsync(ct);
        // Step 6: raise this month's bills for the new student.
        return ToDto(s);
    }

    /// <summary>PUT /students/{id} — opting out of an optional fee cancels its unpaid bills.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> Update(Guid id, StudentInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        var (clean, error) = await Validate(input, id, ct);
        if (error is not null) return BadRequest(new { message = error });

        var droppedHeads = s.FeeHeads.Select(h => h.Id).Except(clean!.OptionalHeadIds).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Apply(s, clean);
        s.FeeHeads.Clear();
        foreach (var head in await LoadOptionalHeads(clean.OptionalHeadIds, ct)) s.FeeHeads.Add(head);
        await db.SaveChangesAsync(ct);

        if (droppedHeads.Count > 0) await CancelUnpaidBills(s.Id, droppedHeads, ct);
        await tx.CommitAsync(ct);

        return ToDto(s);
    }

    /// <summary>DELETE /students/{id} — soft delete: history and paid fees are kept, unpaid bills cancelled.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || !s.IsActive) return NoContent();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        s.IsActive = false;
        s.LeftOn = Clock.Today;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await CancelUnpaidBills(s.Id, null, ct);
        await tx.CommitAsync(ct);

        return NoContent();
    }

    /// <summary>POST /students/{id}/concessions</summary>
    [HttpPost("{id:guid}/concessions")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> AddConcession(Guid id, ConcessionInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        if (!Reasons.Contains(input.Reason)) return BadRequest(new { message = "Choose a reason for the discount." });
        if (input.Kind is not ("Percent" or "Amount")) return BadRequest(new { message = "Choose percent or amount." });
        if (input.Value <= 0) return BadRequest(new { message = "Enter a discount above zero." });
        if (input.Kind == "Percent" && input.Value > 100) return BadRequest(new { message = "A percent discount can't be more than 100." });

        Guid? headId = null;
        if (!string.IsNullOrEmpty(input.FeeHeadId))
        {
            if (!Guid.TryParse(input.FeeHeadId, out var h) || !await db.FeeHeads.AnyAsync(x => x.Id == h && !x.IsDeleted, ct))
                return BadRequest(new { message = "That fee no longer exists." });
            headId = h;
        }

        s.StudentConcessions.Add(new StudentConcession
        {
            TenantId = s.TenantId,
            Reason = input.Reason,
            Note = input.Note?.Trim() ?? "",
            FeeHeadId = headId,
            Kind = input.Kind,
            Value = input.Value,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        // Step 6: re-price this student's unpaid bills.
        return ToDto(s);
    }

    /// <summary>DELETE /students/{id}/concessions/{concessionId}</summary>
    [HttpDelete("{id:guid}/concessions/{concessionId:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> RemoveConcession(Guid id, Guid concessionId, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        var c = s.StudentConcessions.FirstOrDefault(x => x.Id == concessionId);
        if (c is not null)
        {
            db.StudentConcessions.Remove(c);
            await db.SaveChangesAsync(ct);
            // Step 6: re-price this student's unpaid bills.
        }
        return ToDto(s);
    }

    // ---------- helpers ----------

    private record CleanInput(string Name, string ParentName, string Phone, Guid BatchId, DateOnly JoinedOn, List<Guid> OptionalHeadIds);

    private async Task<(CleanInput?, string?)> Validate(StudentInput i, Guid? editingId, CancellationToken ct)
    {
        var name = i.Name?.Trim() ?? "";
        if (name.Length == 0) return (null, "Enter the student's name.");

        var phone = Phone.Normalise(i.ParentPhone);
        if (phone is null) return (null, "Enter a valid 10-digit mobile number.");

        if (!Guid.TryParse(i.BatchId, out var batchId) || !await db.Batches.AnyAsync(b => b.Id == batchId, ct))
            return (null, "Choose a batch or class.");

        var joinedOn = Clock.Today;
        if (!string.IsNullOrWhiteSpace(i.JoinedOn) && !DateOnly.TryParse(i.JoinedOn, out joinedOn))
            return (null, "Enter a valid joining date.");

        var headIds = new List<Guid>();
        foreach (var raw in i.OptionalHeadIds ?? [])
        {
            if (!Guid.TryParse(raw, out var h)) return (null, "One of the chosen fees no longer exists.");
            headIds.Add(h);
        }
        headIds = headIds.Distinct().ToList();
        if (headIds.Count > 0 &&
            await db.FeeHeads.CountAsync(h => headIds.Contains(h.Id) && h.IsOptional && !h.IsDeleted, ct) != headIds.Count)
            return (null, "One of the chosen fees no longer exists.");

        if (await db.Students.AnyAsync(s => s.IsActive && s.ParentPhone == phone && s.Name == name && s.Id != editingId, ct))
            return (null, $"{name} is already added with this number.");

        return (new CleanInput(name, i.ParentName?.Trim() ?? "", phone, batchId, joinedOn, headIds), null);
    }

    private static void Apply(Student s, CleanInput c)
    {
        s.Name = c.Name;
        s.ParentName = c.ParentName;
        s.ParentPhone = c.Phone;
        s.BatchId = c.BatchId;
        s.JoinedOn = c.JoinedOn;
        s.UpdatedAt = DateTime.UtcNow;
    }

    private Task<List<FeeHead>> LoadOptionalHeads(List<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult(new List<FeeHead>())
                       : db.FeeHeads.Where(h => ids.Contains(h.Id)).ToListAsync(ct);

    /// <summary>Bills with no money against them are cancelled; part-paid bills stay so the balance is still owed.</summary>
    private Task<int> CancelUnpaidBills(Guid studentId, List<Guid>? onlyHeads, CancellationToken ct) =>
        db.FeeDues
          .Where(d => d.StudentId == studentId && d.PaidAmount == 0 && d.Status != "Cancelled"
                      && (onlyHeads == null || onlyHeads.Contains(d.FeeHeadId)))
          .ExecuteUpdateAsync(u => u
              .SetProperty(d => d.Status, "Cancelled")
              .SetProperty(d => d.CancelledAt, DateTime.UtcNow), ct);

    private IQueryable<Student> WithDetails() =>
        db.Students.Include(s => s.FeeHeads).Include(s => s.StudentConcessions);

    private Task<bool> TeacherCanSee(Guid batchId, CancellationToken ct) =>
        db.Batches.AnyAsync(b => b.Id == batchId && b.Users.Any(u => u.Id == current.UserId), ct);

    /// <summary>Random, unguessable id for the parent payment link (/pay/{token}).</summary>
    internal static string NewPayToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    internal static StudentDto ToDto(Student s) => new(
        s.Id.ToString(), s.Name, s.ParentName, s.ParentPhone, s.BatchId.ToString(),
        s.JoinedOn.ToString("yyyy-MM-dd"), s.IsActive,
        s.FeeHeads.Select(h => h.Id.ToString()).ToArray(),
        s.StudentConcessions.OrderBy(c => c.CreatedAt)
            .Select(c => new ConcessionDto(c.Id.ToString(), c.Reason, c.Note, c.FeeHeadId?.ToString(), c.Kind, c.Value))
            .ToArray());
}