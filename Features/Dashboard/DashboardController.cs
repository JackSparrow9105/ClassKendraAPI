using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Dashboard;

// Matches DashboardSummary in models.ts.
public record DashboardSummary(
    string Month, int Collected, int Expected, int Pending, int PendingCount,
    int DueThisWeek, int DueThisWeekCount, int PresentToday, int MarkedToday,
    int StudentCount, bool FeesConfigured, List<StudentFees> NeedsAttention);

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Roles = "Owner,Staff")]
public class DashboardController(ClassKendraContext db, BillingService billing) : ControllerBase
{
    /// <summary>GET /dashboard — the home screen numbers (same rules as the app's mock).</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardSummary>> Get(CancellationToken ct)
    {
        var today = Clock.Today;
        var month = today.ToString("yyyy-MM");
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var weekEnd = today.AddDays(7);

        await billing.RaiseAsync(null, ct);   // make sure this month's bills exist

        // Money received this month (by receipt date) against what was billed this month.
        var collected = await db.Payments
            .Where(p => p.PaidOn >= monthStart && p.PaidOn < monthEnd)
            .SumAsync(p => (int?)p.Amount, ct) ?? 0;
        var expected = await db.FeeDues
            .Where(d => d.Month == month && d.Status != DueStatus.Cancelled)
            .SumAsync(d => d.Amount, ct) ?? 0;

        // Everything still owed on bills raised up to this month.
        var unpaid = await db.FeeDues.AsNoTracking()
            .Include(d => d.Student).Include(d => d.Batch)
            .Where(d => (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial) && string.Compare(d.Month, month) <= 0)
            .ToListAsync(ct);
        int Balance(Data.Entities.FeeDue d) => (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount;

        var dueSoon = unpaid.Where(d => d.DueDate >= today && d.DueDate <= weekEnd).ToList();

        // Attendance: students in batches marked today, and how many of them were present.
        var sheets = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.Date == today)
            .Select(a => new
            {
                a.BatchId,
                Absent = a.AttendanceAbsences.Count(x => x.Student.IsActive)
            })
            .ToListAsync(ct);
        var markedBatchIds = sheets.Select(s => s.BatchId).ToList();
        var marked = markedBatchIds.Count == 0 ? 0
            : await db.Students.CountAsync(s => s.IsActive && markedBatchIds.Contains(s.BatchId), ct);

        var studentCount = await db.Students.CountAsync(s => s.IsActive, ct);
        var feesConfigured = await db.FeeSettings.AnyAsync(ct) &&
                             await db.FeeHeads.AnyAsync(h => !h.IsDeleted && h.FeeHeadAmounts.Any(a => a.Amount > 0), ct);

        var needsAttention = StudentFeesRows.Build(unpaid, today)
            .Where(r => r.MaxDaysLate > 0)
            .OrderByDescending(r => r.MaxDaysLate)
            .Take(4)
            .ToList();

        return new DashboardSummary(month, collected, expected,
            unpaid.Sum(Balance), unpaid.Select(d => d.StudentId).Distinct().Count(),
            dueSoon.Sum(Balance), dueSoon.Select(d => d.StudentId).Distinct().Count(),
            marked - sheets.Sum(s => s.Absent), marked,
            studentCount, feesConfigured, needsAttention);
    }
}
