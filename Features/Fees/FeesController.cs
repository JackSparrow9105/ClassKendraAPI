using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public record RemindInput(string[] StudentIds);
public record SentResult(int Sent);
public record RaiseResult(int Created);

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
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<StudentFees>>> List(
        [FromQuery] string? month, [FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] Guid? batchId, CancellationToken ct)
    {
        var today = Clock.Today;
        var m = string.IsNullOrWhiteSpace(month) ? today.ToString("yyyy-MM") : month;
        if (!DateOnly.TryParse(m + "-01", out var monthStart)) return BadRequest(new { message = "Choose a valid month." });

        // A visit to the Fees screen also makes sure this month's bills exist (the daily job may not have run yet).
        if (m == today.ToString("yyyy-MM")) await billing.RaiseAsync(null, ct);

        var q = db.FeeDues.AsNoTracking()
            .Include(d => d.Student).Include(d => d.Batch)
            .Where(d => d.Status != DueStatus.Cancelled);

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

        var rows = StudentFeesRows.Build(await q.ToListAsync(ct), today);
        return rows
            .OrderByDescending(r => r.Pending > 0 ? 1 : 0)
            .ThenByDescending(r => r.MaxDaysLate)
            .ThenBy(r => r.StudentName)
            .ToList();
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
