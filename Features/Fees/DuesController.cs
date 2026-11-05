using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public record PendingStudent(
    string StudentId, string StudentName, string BatchId, string BatchName, string ParentPhone,
    int Balance, int DuesCount, string OldestDueDate, int DaysLate, int RemindersSent);

public record RaiseResult(int Created);
public record RemindInput(string[] StudentIds);

[ApiController]
[Route("api/v1/fees")]
[Authorize(Roles = "Owner,Staff")]
public class DuesController(ClassKendraContext db, BillingService billing, ReminderService reminders) : ControllerBase
{
    /// <summary>
    /// GET /fees/dues?month=2026-11&status=&batchId=&search=
    /// status: Pending | Partial | Paid | Unpaid (pending + partial) | Overdue (unpaid and past due date).
    /// Without month, all months are included (useful with status=Overdue).
    /// </summary>
    [HttpGet("dues")]
    public async Task<ActionResult<List<FeeDueView>>> Dues(
        [FromQuery] string? month, [FromQuery] string? status, [FromQuery] Guid? batchId,
        [FromQuery] string? search, CancellationToken ct)
    {
        var today = Clock.Today;
        var q = db.FeeDues.AsNoTracking().Where(d => d.Status != DueStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(month)) q = q.Where(d => d.Month == month);
        if (batchId is Guid b) q = q.Where(d => d.BatchId == b);
        q = status switch
        {
            null or "" => q,
            "Unpaid" => q.Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial),
            "Overdue" => q.Where(d => (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial) && d.DueDate < today),
            _ => q.Where(d => d.Status == status)
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(d => d.Student.Name.Contains(term) || d.Student.ParentPhone.Contains(term));
        }

        var rows = await q
            .OrderBy(d => d.DueDate).ThenBy(d => d.Student.Name).ThenBy(d => d.HeadName)
            .Take(5000)
            .Select(d => new { Due = d, StudentName = d.Student.Name, BatchName = d.Batch.Name })
            .ToListAsync(ct);

        return rows.Select(r => FeeViews.ToView(r.Due, r.StudentName, r.BatchName, today)).ToList();
    }

    /// <summary>GET /fees/pending?batchId= — every student who owes money, most overdue first.</summary>
    [HttpGet("pending")]
    public async Task<ActionResult<List<PendingStudent>>> Pending([FromQuery] Guid? batchId, CancellationToken ct)
    {
        var today = Clock.Today;
        var q = db.FeeDues.AsNoTracking()
            .Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial);
        if (batchId is Guid b) q = q.Where(d => d.Student.BatchId == b);

        var groups = await q
            .GroupBy(d => d.StudentId)
            .Select(g => new
            {
                StudentId = g.Key,
                Balance = g.Sum(d => (d.Amount ?? 0) - d.PaidAmount),
                Count = g.Count(),
                Oldest = g.Min(d => d.DueDate),
                Reminders = g.Max(d => d.RemindersSent)
            })
            .Where(x => x.Balance > 0)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.StudentId).ToList();
        var students = await db.Students.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.BatchId, BatchName = s.Batch.Name, s.ParentPhone })
            .ToDictionaryAsync(s => s.Id, ct);

        return groups
            .Where(g => students.ContainsKey(g.StudentId))
            .Select(g =>
            {
                var s = students[g.StudentId];
                return new PendingStudent(s.Id.ToString(), s.Name, s.BatchId.ToString(), s.BatchName, s.ParentPhone,
                    g.Balance, g.Count, g.Oldest.ToString("yyyy-MM-dd"),
                    Math.Max(0, today.DayNumber - g.Oldest.DayNumber), g.Reminders);
            })
            .OrderByDescending(p => p.DaysLate).ThenByDescending(p => p.Balance)
            .ToList();
    }

    /// <summary>POST /fees/raise — raise this month's bills now (the daily job does this too).</summary>
    [HttpPost("raise")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<RaiseResult>> Raise(CancellationToken ct) =>
        new RaiseResult(await billing.RaiseAsync(null, ct));

    /// <summary>POST /fees/remind { studentIds } — WhatsApp reminder now (max one per student per day).</summary>
    [HttpPost("remind")]
    public async Task<ActionResult<RemindResult>> Remind(RemindInput input, CancellationToken ct)
    {
        var ids = (input.StudentIds ?? []).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one student." });
        if (ids.Count > 1000) return BadRequest(new { message = "Send to at most 1,000 students at a time." });

        var result = await reminders.SendManualAsync(ids, ct);
        if (result.LimitReached && result.Sent == 0)
            return BadRequest(new { message = "This month's WhatsApp messages are used up. Upgrade your plan to send more." });
        return result;
    }
}
