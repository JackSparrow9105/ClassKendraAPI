using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Reports;

// Matches CollectionReport in models.ts.
public record ModeTotal(string Mode, int Amount, int Count);
public record GroupTotal(string BatchId, string Name, int Collected, int Pending);
public record HeadTotal(string HeadName, int Collected);
public record CollectionReport(
    string Month, int Collected, int ReceiptCount, int DiscountGiven,
    List<ModeTotal> ByMode, List<GroupTotal> ByGroup, List<HeadTotal> ByHead, List<PaymentView> Payments);

/// <summary>
/// The Excel download is built in the browser from this same JSON (exceljs), so there is no .xlsx endpoint.
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize(Roles = "Owner,Staff")]
public class ReportsController(ClassKendraContext db, FeeReadService views) : ControllerBase
{
    private static readonly string[] Modes = ["Cash", "UPI", "Cheque", "Online"];

    /// <summary>
    /// GET /reports/collection?month=yyyy-MM
    /// Collected = money received in the month (by receipt date).
    /// Pending = balance still owed on bills raised up to that month.
    /// </summary>
    [HttpGet("collection")]
    public async Task<ActionResult<CollectionReport>> Collection([FromQuery] string? month, CancellationToken ct)
    {
        var m = string.IsNullOrWhiteSpace(month) ? Clock.Today.ToString("yyyy-MM") : month;
        if (!DateOnly.TryParse(m + "-01", out var start)) return BadRequest(new { message = "Choose a valid month." });
        var end = start.AddMonths(1);

        var payments = await db.Payments.AsNoTracking()
            .Include(p => p.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .Where(p => p.PaidOn >= start && p.PaidOn < end)
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.ReceiptNo)
            .ToListAsync(ct);

        var byMode = Modes
            .Select(mode => payments.Where(p => p.Mode == mode).ToList())
            .Where(list => list.Count > 0)
            .Select(list => new ModeTotal(list[0].Mode, list.Sum(p => p.Amount), list.Count))
            .ToList();

        // Collected per batch uses the student's current batch (as the app does).
        var studentIds = payments.Select(p => p.StudentId).Distinct().ToList();
        var studentBatch = await db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.BatchId, ct);

        var pendingByBatch = await db.FeeDues.AsNoTracking()
            .Where(d => (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial) && string.Compare(d.Month, m) <= 0)
            .GroupBy(d => d.BatchId)
            .Select(g => new { g.Key, Pending = g.Sum(d => (d.Amount ?? 0) - d.PaidAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Pending, ct);

        var batches = await db.Batches.AsNoTracking().OrderBy(b => b.Name).Select(b => new { b.Id, b.Name }).ToListAsync(ct);
        var byGroup = batches.Select(b => new GroupTotal(b.Id.ToString(), b.Name,
            payments.Where(p => studentBatch.GetValueOrDefault(p.StudentId) == b.Id).Sum(p => p.Amount),
            pendingByBatch.GetValueOrDefault(b.Id))).ToList();

        var byHead = payments.SelectMany(p => p.PaymentAllocations)
            .GroupBy(a => a.FeeDue.HeadName)
            .Select(g => new HeadTotal(g.Key, g.Sum(a => a.Amount)))
            .OrderByDescending(h => h.Collected)
            .ToList();

        var discountGiven = await db.FeeDues
            .Where(d => d.Month == m && d.Status != DueStatus.Cancelled)
            .SumAsync(d => (int?)d.Discount, ct) ?? 0;

        return new CollectionReport(m, payments.Sum(p => p.Amount), payments.Count, discountGiven,
            byMode, byGroup, byHead, await views.PaymentViews(payments, ct));
    }
}
