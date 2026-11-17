using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;
public record RemindInput(string[] StudentIds);
public record SentResult(int Sent);
public record RaiseResult(int Created);

/// <summary>
/// One page of the Fees screen (one row per student). The three totals cover ALL students that match the
/// filters, not only this page: PendingTotal ₹ and PendingCount students still owing, PaidTotal ₹ paid on these bills.
/// </summary>
public record FeesPage(List<StudentFees> Items, int Total, int Page, int PageSize,
                       int PendingTotal, int PendingCount, int PaidTotal);

[ApiController]
[Route("api/v1/fees")]
[Authorize(Roles = "Owner,Staff")]
public class FeesController(ClassKendraContext db, BillingService billing, ReminderService reminders) : ControllerBase
{
    /// <summary>
    /// GET /fees?month=2026-11&status=Pending|Paid|All&search=&batchId= → StudentFees[] (one row per student)
    ///   Pending = unpaid or part-paid bills raised up to that month (older months included)
    ///   Paid    = bills of that month that are fully paid
    ///   All     = that month's bills plus older unpaid ones
    /// Everything at once. The Fees screen uses /fees/page.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<StudentFees>>> List(
        [FromQuery] string? month, [FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] Guid? batchId, CancellationToken ct)
    {
        var today = Clock.Today;
        var q = await FilterAsync(month, status, search, batchId, today, ct);
        if (q is null) return BadRequest(new { message = "Choose a valid month." });

        var rows = StudentFeesRows.Build(await q.Include(d => d.Student).Include(d => d.Batch).ToListAsync(ct), today);
        return rows
            .OrderByDescending(r => r.Pending > 0 ? 1 : 0)
            .ThenByDescending(r => r.MaxDaysLate)
            .ThenBy(r => r.StudentName)
            .ToList();
    }

    /// <summary>
    /// GET /fees/page?month=&status=&search=&batchId=&page=&pageSize= → FeesPage
    /// Same filters and order as GET /fees (still owing first, most days late first, then by name),
    /// 50 students at a time, with totals for everyone who matches.
    /// </summary>
    [HttpGet("page")]
    public async Task<ActionResult<FeesPage>> Page(
        [FromQuery] string? month, [FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] Guid? batchId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var (p, size, skip) = Paging.Clean(page, pageSize);
        var today = Clock.Today;
        var q = await FilterAsync(month, status, search, batchId, today, ct);
        if (q is null) return BadRequest(new { message = "Choose a valid month." });

        // One line per student, worked out in the database.
        var perStudent = q
            .GroupBy(d => new { d.StudentId, d.Student.Name })
            .Select(g => new
            {
                g.Key.StudentId,
                g.Key.Name,
                Pending = g.Sum(d => d.Status != DueStatus.Paid ? d.GrossAmount - d.Discount - d.PaidAmount : 0),
                Paid = g.Sum(d => d.PaidAmount),
                OldestDue = g.Min(d => d.Status != DueStatus.Paid ? (DateOnly?)d.DueDate : null)
            });

        var totals = await perStudent
            .GroupBy(x => 1)
            .Select(g => new
            {
                Students = g.Count(),
                PendingTotal = g.Sum(x => x.Pending),
                PendingCount = g.Sum(x => x.Pending > 0 ? 1 : 0),
                PaidTotal = g.Sum(x => x.Paid)
            })
            .FirstOrDefaultAsync(ct);
        if (totals is null) return new FeesPage([], 0, p, size, 0, 0, 0);

        // Still owing first; among those the longest overdue first (not-yet-due all count the same); then by name.
        var ids = await perStudent
            .OrderBy(x => x.Pending > 0 ? 0 : 1)
            .ThenBy(x => x.OldestDue == null || x.OldestDue > today ? today : x.OldestDue)
            .ThenBy(x => x.Name).ThenBy(x => x.StudentId)
            .Skip(skip).Take(size)
            .Select(x => x.StudentId)
            .ToListAsync(ct);

        var bills = ids.Count == 0
            ? []
            : await q.Where(d => ids.Contains(d.StudentId)).Include(d => d.Student).Include(d => d.Batch).ToListAsync(ct);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id.ToString(), x => x.i);
        var rows = StudentFeesRows.Build(bills, today).OrderBy(r => order[r.StudentId]).ToList();

        return new FeesPage(rows, totals.Students, p, size, totals.PendingTotal, totals.PendingCount, totals.PaidTotal);
    }

    /// <summary>The bills behind the Fees screen for these filters. Null = the month isn't valid.</summary>
    private async Task<IQueryable<Data.Entities.FeeDue>?> FilterAsync(
        string? month, string? status, string? search, Guid? batchId, DateOnly today, CancellationToken ct)
    {
        var m = string.IsNullOrWhiteSpace(month) ? today.ToString("yyyy-MM") : month;
        if (!DateOnly.TryParse(m + "-01", out _)) return null;

        // A visit to the Fees screen also makes sure this month's bills exist (the daily job may not have run yet).
        if (m == today.ToString("yyyy-MM")) await billing.RaiseAsync(null, ct);

        var q = db.FeeDues.AsNoTracking().Where(d => d.Status != DueStatus.Cancelled);

        q = status switch
        {
            "Paid" => q.Where(d => d.Status == DueStatus.Paid && d.Month == m),
            "All" => q.Where(d => d.Month == m || (d.Status != DueStatus.Paid && string.Compare(d.Month, m) < 0)),
            _ => q.Where(d => d.Status != DueStatus.Paid && string.Compare(d.Month, m) <= 0)
        };
        if (batchId is Guid b) q = q.Where(d => d.BatchId == b);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(d => d.Student.Name.Contains(term) || d.Batch.Name.Contains(term));
        }
        return q;
    }

    /// <summary>POST /fees/remind { studentIds } — one WhatsApp per family listing everything unpaid.</summary>
    [HttpPost("remind")]
    public async Task<ActionResult<SentResult>> Remind(RemindInput input, CancellationToken ct)
    {
        var ids = (input.StudentIds ?? []).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one student." });
        if (ids.Count > 2000) return BadRequest(new { message = "Send to at most 2,000 families at a time." });
        return Result(await reminders.SendManualAsync(ids, ct));
    }

    /// <summary>POST /fees/remind-all?month= — every family with something unpaid up to that month.</summary>
    [HttpPost("remind-all")]
    public async Task<ActionResult<SentResult>> RemindAll([FromQuery] string? month, CancellationToken ct)
    {
        var m = string.IsNullOrWhiteSpace(month) ? Clock.Today.ToString("yyyy-MM") : month;
        if (!DateOnly.TryParse(m + "-01", out _)) return BadRequest(new { message = "Choose a valid month." });
        return Result(await reminders.SendAllAsync(m, ct));
    }

    /// <summary>POST /fees/raise — raise this month's bills now (the daily job does this too).</summary>
    [HttpPost("raise")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<RaiseResult>> Raise(CancellationToken ct) =>
        new RaiseResult(await billing.RaiseAsync(null, ct));

    private ActionResult<SentResult> Result(RemindResult r) =>
        r.LimitReached && r.Sent == 0
            ? BadRequest(new { message = "This month's WhatsApp messages are used up. Upgrade your plan to send more." })
            : new SentResult(r.Sent);
}
