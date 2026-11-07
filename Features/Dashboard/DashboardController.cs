using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Dashboard;

// ---- Shapes that match models.ts (DashboardSummary, StudentFees, DashboardInsights) ----

public record DashboardSummary(
    string Month, int Collected, int Expected, int Pending, int PendingCount,
    int DueThisWeek, int DueThisWeekCount, int PresentToday, int MarkedToday,
    int StudentCount, bool FeesConfigured, List<StudentFees> NeedsAttention, DashboardInsights Insights);

public record TrendPoint(string Month, int Billed, int Collected);
public record BatchCollection(string BatchId, string Name, int Billed, int Collected);
public record RecentPayment(string Id, string ReceiptNo, string StudentName, string BatchName,
    int Amount, string Mode, string PaidOn, string? At);
public record ClassToday(string BatchId, string Name, string? StartTime, string? Teacher, int Students, int? Present);

public record DashboardInsights(
    int NewThisMonth, int Overdue, int OverdueCount, int DigitalSharePct,
    int MessagesThisMonth, int MessagesDeliveredPct, int MessageLimit,
    List<TrendPoint> Trend, List<BatchCollection> Batches,
    List<RecentPayment> RecentPayments, List<ClassToday> ClassesToday);

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Roles = "Owner,Staff")]
public class DashboardController(ClassKendraContext db, BillingService billing) : ControllerBase
{
    private const int LateRows = 7;

    /// <summary>GET /dashboard — home screen numbers, plus `insights` for the wide desktop layout.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardSummary>> Get(CancellationToken ct)
    {
        var today = Clock.Today;
        var month = today.ToString("yyyy-MM");
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var weekEnd = today.AddDays(7);

        await billing.RaiseAsync(null, ct);   // make sure this month's bills exist

        // ---------- Money ----------
        var monthPayments = await db.Payments.AsNoTracking()
            .Where(p => p.PaidOn >= monthStart && p.PaidOn < monthEnd)
            .Select(p => new { p.Amount, p.Mode })
            .ToListAsync(ct);
        var collected = monthPayments.Sum(p => p.Amount);
        var digital = monthPayments.Where(p => p.Mode == "UPI" || p.Mode == "Online").Sum(p => p.Amount);

        var monthDues = await db.FeeDues.AsNoTracking()
            .Where(d => d.Month == month && d.Status != "Cancelled")
            .Select(d => new { d.BatchId, Amount = d.Amount ?? d.GrossAmount - d.Discount, d.PaidAmount })
            .ToListAsync(ct);
        var expected = monthDues.Sum(d => d.Amount);

        // Everything still owed on bills raised up to this month (older unpaid months included).
        var unpaid = await db.FeeDues.AsNoTracking()
            .Include(d => d.Student).Include(d => d.Batch)
            .Where(d => (d.Status == "Pending" || d.Status == "Partial") && string.Compare(d.Month, month) <= 0)
            .ToListAsync(ct);
        static int Balance(Data.Entities.FeeDue d) => (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount;

        var dueSoon = unpaid.Where(d => d.DueDate >= today && d.DueDate <= weekEnd).ToList();
        var overdue = unpaid.Where(d => d.DueDate < today).ToList();

        // ---------- Attendance ----------
        var sheets = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.Date == today)
            .Select(a => new { a.BatchId, Absent = a.AttendanceAbsences.Count(x => x.Student.IsActive) })
            .ToListAsync(ct);
        var absentByBatch = sheets.ToDictionary(s => s.BatchId, s => s.Absent);

        var studentsByBatch = await db.Students.AsNoTracking()
            .Where(s => s.IsActive)
            .GroupBy(s => s.BatchId)
            .Select(g => new { BatchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BatchId, g => g.Count, ct);

        var markedToday = sheets.Sum(s => studentsByBatch.GetValueOrDefault(s.BatchId));
        var presentToday = Math.Max(0, markedToday - sheets.Sum(s => s.Absent));
        var studentCount = studentsByBatch.Values.Sum();

        var feesConfigured = await db.FeeSettings.AnyAsync(ct) &&
                             await db.FeeHeads.AnyAsync(h => !h.IsDeleted && h.FeeHeadAmounts.Any(a => a.Amount > 0), ct);

        // ---------- Late fees (most overdue first) ----------
        var needsAttention = StudentFeesRows.Build(unpaid, today)
            .Where(r => r.MaxDaysLate > 0)
            .OrderByDescending(r => r.MaxDaysLate)
            .ThenByDescending(r => r.Pending)
            .Take(LateRows)
            .ToList();

        var insights = await InsightsAsync(today, monthStart, monthDues.Select(d => (d.BatchId, d.Amount, d.PaidAmount)).ToList(),
            collected, digital, overdue.Sum(Balance), overdue.Select(d => d.StudentId).Distinct().Count(),
            studentsByBatch, absentByBatch, ct);

        return new DashboardSummary(month, collected, expected,
            unpaid.Sum(Balance), unpaid.Select(d => d.StudentId).Distinct().Count(),
            dueSoon.Sum(Balance), dueSoon.Select(d => d.StudentId).Distinct().Count(),
            presentToday, markedToday, studentCount, feesConfigured, needsAttention, insights);
    }

    private async Task<DashboardInsights> InsightsAsync(
        DateOnly today, DateOnly monthStart, List<(Guid BatchId, int Amount, int PaidAmount)> monthDues,
        int collected, int digital, int overdue, int overdueCount,
        Dictionary<Guid, int> studentsByBatch, Dictionary<Guid, int> absentByBatch, CancellationToken ct)
    {
        var newThisMonth = await db.Students.CountAsync(s => s.IsActive && s.JoinedOn >= monthStart, ct);

        // WhatsApp: messages created since the 1st of this month (India time), stored in UTC.
        var monthStartUtc = monthStart.ToDateTime(TimeOnly.MinValue).AddMinutes(-330);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= monthStartUtc)
            .GroupBy(m => m.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var messageCount = messages.Sum(m => m.Count);
        var sent = messages.Where(m => m.Status != "Queued").Sum(m => m.Count);
        var delivered = messages.Where(m => m.Status == "Delivered" || m.Status == "Read").Sum(m => m.Count);
        var messageLimit = await db.Tenants.Select(t => t.MessageLimit).FirstOrDefaultAsync(ct);

        // Last 6 months: billed = bills raised that month, collected = receipts dated that month.
        var trendStart = monthStart.AddMonths(-5);
        var trendKeys = Enumerable.Range(0, 6).Select(i => trendStart.AddMonths(i).ToString("yyyy-MM")).ToList();
        var billedByMonth = (await db.FeeDues.AsNoTracking()
                .Where(d => trendKeys.Contains(d.Month) && d.Status != "Cancelled")
                .Select(d => new { d.Month, Amount = d.Amount ?? d.GrossAmount - d.Discount })
                .ToListAsync(ct))
            .GroupBy(d => d.Month)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        var collectedByMonth = (await db.Payments.AsNoTracking()
                .Where(p => p.PaidOn >= trendStart)
                .Select(p => new { p.PaidOn, p.Amount })
                .ToListAsync(ct))
            .GroupBy(p => p.PaidOn.ToString("yyyy-MM"))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var trend = trendKeys
            .Select(k => new TrendPoint(k, billedByMonth.GetValueOrDefault(k), collectedByMonth.GetValueOrDefault(k)))
            .ToList();

        // Batches: names, days and the first teacher assigned to each.
        var batches = await db.Batches.AsNoTracking()
            .Select(b => new
            {
                b.Id, b.Name, b.Days, b.StartTime,
                Teacher = b.Users.Where(u => u.Role == "Teacher" && u.IsActive).Select(u => u.Name).FirstOrDefault()
            })
            .ToListAsync(ct);

        var batchCollection = monthDues
            .GroupBy(d => d.BatchId)
            .Select(g => new BatchCollection(
                g.Key.ToString(),
                batches.FirstOrDefault(b => b.Id == g.Key)?.Name ?? "",
                g.Sum(d => d.Amount), g.Sum(d => d.PaidAmount)))
            .Where(b => b.Billed > 0)
            .OrderBy(b => b.Name)
            .ToList();

        var recent = await db.Payments.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .Take(6)
            .Select(p => new { p.Id, p.ReceiptNo, Student = p.Student.Name, Batch = p.Student.Batch.Name, p.Amount, p.Mode, p.PaidOn, p.CreatedAt })
            .ToListAsync(ct);
        var recentPayments = recent
            .Select(p => new RecentPayment(p.Id.ToString(), p.ReceiptNo, p.Student ?? "", p.Batch ?? "",
                p.Amount, p.Mode, p.PaidOn.ToString("yyyy-MM-dd"), $"{p.CreatedAt:yyyy-MM-ddTHH:mm:ss}Z"))
            .ToList();

        var classesToday = batches
            .Where(b => ClassDays.MeetsOn(b.Days, today))
            .OrderBy(b => b.StartTime ?? TimeOnly.MaxValue)
            .Select(b =>
            {
                var students = studentsByBatch.GetValueOrDefault(b.Id);
                int? present = absentByBatch.TryGetValue(b.Id, out var absent) ? Math.Max(0, students - absent) : null;
                return new ClassToday(b.Id.ToString(), b.Name, b.StartTime?.ToString("HH:mm"), b.Teacher, students, present);
            })
            .ToList();

        return new DashboardInsights(
            newThisMonth, overdue, overdueCount,
            collected == 0 ? 0 : (int)Math.Round(digital * 100.0 / collected),
            messageCount, sent == 0 ? 0 : (int)Math.Round(delivered * 100.0 / sent), messageLimit,
            trend, batchCollection, recentPayments, classesToday);
    }
}

/// <summary>
/// Does a batch meet on a date? Understands "Mon,Wed,Fri", "Mon, Wed, Fri", "Mon–Sat", "Mon-Fri" and "Daily".
/// No days (schools) = Monday to Saturday. Same rules as meetsOn() in the app (shared/class-days.ts).
/// </summary>
public static class ClassDays
{
    private static readonly string[] Names = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static bool MeetsOn(string? days, DateOnly date)
    {
        var today = (int)date.DayOfWeek;
        var text = (days ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return today != 0;
        if (text.Contains("daily") || text.Contains("every day") || text.Contains("everyday")) return true;

        var range = text.Split(['–', '—', '-'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (range.Length == 2 && !text.Contains(','))
        {
            var from = Array.IndexOf(Names, range[0].Length >= 3 ? range[0][..3] : range[0]);
            var to = Array.IndexOf(Names, range[1].Length >= 3 ? range[1][..3] : range[1]);
            if (from < 0 || to < 0) return false;
            return from <= to ? today >= from && today <= to : today >= from || today <= to;
        }

        return text.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(d => d.Length >= 3 && d[..3] == Names[today]);
    }
}
