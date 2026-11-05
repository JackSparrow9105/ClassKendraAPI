using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Dashboard;

// Matches DashboardSummary in models.ts. NeedsAttention is filled in Step 6 (it needs the StudentFees rows).
public record DashboardSummary(
    string Month, int Collected, int Expected, int Pending, int PendingCount,
    int DueThisWeek, int DueThisWeekCount, int PresentToday, int MarkedToday,
    int StudentCount, bool FeesConfigured, object[] NeedsAttention);

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Roles = "Owner,Staff")]
public class DashboardController(ClassKendraContext db) : ControllerBase
{
    /// <summary>GET /dashboard — this month's numbers for the home screen.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardSummary>> Get(CancellationToken ct)
    {
        var today = Clock.Today;
        var month = today.ToString("yyyy-MM");
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var weekEnd = today.AddDays(7);

        var monthDues = db.FeeDues.Where(d => d.Month == month && d.Status != "Cancelled");
        var unpaid = db.FeeDues.Where(d => d.Status == "Pending" || d.Status == "Partial");

        var collected = await db.Payments
            .Where(p => p.PaidOn >= monthStart && p.PaidOn < monthEnd)
            .SumAsync(p => (int?)p.Amount, ct) ?? 0;

        var expected = await monthDues.SumAsync(d => (int?)d.Amount, ct) ?? 0;
        var pending = await monthDues.Where(d => d.Status != "Paid")
            .SumAsync(d => (int?)(d.Amount - d.PaidAmount), ct) ?? 0;
        var pendingCount = await monthDues.Where(d => d.Status != "Paid")
            .Select(d => d.StudentId).Distinct().CountAsync(ct);

        var dueWeek = unpaid.Where(d => d.DueDate >= today && d.DueDate <= weekEnd);
        var dueThisWeek = await dueWeek.SumAsync(d => (int?)(d.Amount - d.PaidAmount), ct) ?? 0;
        var dueThisWeekCount = await dueWeek.Select(d => d.StudentId).Distinct().CountAsync(ct);

        var sheetsToday = db.AttendanceSheets.Where(a => a.Date == today);
        var markedToday = await sheetsToday.CountAsync(ct);
        var presentToday = await sheetsToday.SumAsync(a => (int?)a.PresentCount, ct) ?? 0;

        var studentCount = await db.Students.CountAsync(s => s.IsActive, ct);
        var feesConfigured = await db.FeeHeads.AnyAsync(h => !h.IsDeleted, ct);

        return new DashboardSummary(month, collected, expected, pending, pendingCount,
            dueThisWeek, dueThisWeekCount, presentToday, markedToday,
            studentCount, feesConfigured, []);
    }
}