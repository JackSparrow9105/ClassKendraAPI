using System.Text.RegularExpressions;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

// ---- Shapes that match models.ts / student.service.ts ----
public record StudentImportRow(string Name, string ParentPhone, string BatchName);
public record ImportSkip(int Row, string Reason);
public record ImportResult(int Added, List<ImportSkip> Skipped);

public record PromotionMove(string FromBatchId, string? ToBatchId);
public record PromotionRequest(List<PromotionMove> Moves, List<string>? KeepBackStudentIds);
public record PromotionResult(int Moved, int Left, int KeptBack);

[ApiController]
[Route("api/v1/students")]
[Authorize]
public class StudentToolsController(ClassKendraContext db, CurrentUser current, BillingService billing, PlanService plans) : ControllerBase
{
    /// <summary>POST /students/import — rows from the Excel/CSV screen.</summary>
    [HttpPost("import")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<ImportResult>> Import(List<StudentImportRow> rows, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });
        if (rows.Count > 5000)
            return BadRequest(new { message = "Please import at most 5,000 students at a time." });

        // Loose match so "Class 8 · A", "8-A" and "8A" all find the same class.
        var batches = await db.Batches.AsNoTracking().ToListAsync(ct);
        var batchByKey = batches.GroupBy(b => Norm(b.Name)).ToDictionary(g => g.Key, g => g.First().Id);

        var existing = (await db.Students.Where(s => s.IsActive)
                .Select(s => new { s.Name, s.ParentPhone }).ToListAsync(ct))
            .Select(x => Key(x.Name, x.ParentPhone))
            .ToHashSet();

        var result = new ImportResult(0, []);
        var added = 0;
        var newStudents = new List<Student>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var rowNo = i + 2;   // row 1 is the header in Excel
            var name = r.Name?.Trim() ?? "";
            var phone = Phone.Normalise(r.ParentPhone);

            if (name.Length == 0) { result.Skipped.Add(new(rowNo, "Name is empty")); continue; }
            if (phone is null) { result.Skipped.Add(new(rowNo, "Phone number is not a valid 10-digit mobile")); continue; }
            if (!batchByKey.TryGetValue(Norm(r.BatchName ?? ""), out var batchId))
            { result.Skipped.Add(new(rowNo, $"\"{r.BatchName}\" doesn't match any batch or class")); continue; }
            if (!existing.Add(Key(name, phone)))
            { result.Skipped.Add(new(rowNo, "Already added with this number")); continue; }

            var student = new Student
            {
                TenantId = tenantId,
                Name = name,
                ParentName = "",
                ParentPhone = phone,
                BatchId = batchId,
                JoinedOn = Clock.Today,
                IsActive = true,
                PayToken = StudentsController.NewPayToken(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Students.Add(student);
            newStudents.Add(student);
            added++;
        }

        if (added > 0 && await plans.StudentLimitErrorAsync(added, ct) is { } limit)
            return BadRequest(new { message = limit });

        await db.SaveChangesAsync(ct);
        if (added > 0) await billing.RaiseAsync(newStudents.Select(s => s.Id).ToList(), ct);
        return result with { Added = added };
    }

    /// <summary>
    /// POST /students/promote — moves whole classes at once, in one transaction,
    /// based on where students are before the promotion. Unpaid fees stay with the student.
    /// </summary>
    [HttpPost("promote")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<PromotionResult>> Promote(PromotionRequest req, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });

        var target = new Dictionary<Guid, Guid?>();
        foreach (var m in req.Moves)
        {
            if (!Guid.TryParse(m.FromBatchId, out var from)) return BadRequest(new { message = "A class in the list no longer exists." });
            Guid? to = null;
            if (m.ToBatchId is not null)
            {
                if (!Guid.TryParse(m.ToBatchId, out var t)) return BadRequest(new { message = "A class in the list no longer exists." });
                to = t;
            }
            target[from] = to;
        }

        var allIds = target.Keys.Concat(target.Values.Where(v => v.HasValue).Select(v => v!.Value)).Distinct().ToList();
        if (await db.Batches.CountAsync(b => allIds.Contains(b.Id), ct) != allIds.Count)
            return BadRequest(new { message = "A class in the list no longer exists." });

        var keepBack = (req.KeepBackStudentIds ?? [])
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).ToHashSet();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var fromIds = target.Keys.ToList();
        var students = await db.Students.Where(s => s.IsActive && fromIds.Contains(s.BatchId)).ToListAsync(ct);

        var run = new PromotionRun { TenantId = tenantId, RunByUserId = current.UserId, CreatedAt = DateTime.UtcNow };
        db.PromotionRuns.Add(run);

        int moved = 0, left = 0, kept = 0;
        foreach (var st in students)
        {
            if (keepBack.Contains(st.Id)) { kept++; continue; }

            var to = target[st.BatchId];
            if (to is null)
            {
                db.StudentBatchHistories.Add(new StudentBatchHistory
                { StudentId = st.Id, FromBatchId = st.BatchId, ToBatchId = null, PromotionRun = run, ChangedAt = DateTime.UtcNow });
                st.IsActive = false;
                st.LeftOn = Clock.Today;
                left++;
            }
            else if (to.Value != st.BatchId)
            {
                db.StudentBatchHistories.Add(new StudentBatchHistory
                { StudentId = st.Id, FromBatchId = st.BatchId, ToBatchId = to, PromotionRun = run, ChangedAt = DateTime.UtcNow });
                st.BatchId = to.Value;
                moved++;
            }
            st.UpdatedAt = DateTime.UtcNow;
        }

        run.Moved = moved;
        run.Left = left;
        run.KeptBack = kept;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Fees of the new class for the current period (bills of the old class stay as they are).
        var movedIds = students.Where(s => s.IsActive && !keepBack.Contains(s.Id)).Select(s => s.Id).ToList();
        if (movedIds.Count > 0) await billing.RaiseAsync(movedIds, ct);
        return new PromotionResult(moved, left, kept);
    }

    private static string Norm(string v) =>
        Regex.Replace(v.ToLowerInvariant().Replace("class", ""), "[^a-z0-9]", "");

    private static string Key(string name, string phone) => $"{name.Trim().ToLowerInvariant()}|{phone}";
}