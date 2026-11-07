using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Dashboard;

// ---- Shapes that match TeacherDashboard in models.ts ----

public record TeacherDashboard(
    int BatchCount, int StudentCount,
    int? MonthPct, int? PrevMonthPct,
    int WeekMarked, int WeekScheduled, int AlertsThisWeek,
    List<TeacherClassToday> ClassesToday,
    List<WatchStudent> Watch,
    List<TeacherBatch> Batches,
    TeacherWeek Week);

public record TeacherClassToday(string BatchId, string Name, string? StartTime, string? EndTime, string? Days,
    int Students, int? Present, string? MarkedAt, int Alerted);

/// <summary>History: one entry per class, oldest first. true = absent.</summary>
public record WatchStudent(string StudentId, string Name, string BatchName, string LastAbsent, int Missed, int Of, List<bool> History);

public record TeacherBatchClass(string Date, int Pct);
public record TeacherBatch(string BatchId, string Name, int Students, int? Pct, List<TeacherBatchClass> Classes, TeacherBatchClass? Lowest);

public record TeacherWeekDay(string Date, bool Scheduled, int? Present, int Students);
public record TeacherWeekSlot(string BatchId, string Name, string? StartTime, List<TeacherWeekDay> Days);
public record TeacherWeek(string Start, List<TeacherWeekSlot> Slots);

/// <summary>
/// GET /dashboard/teacher — the teacher's home screen: their classes and attendance only, no money.
/// Teachers see their own batches; owner and office staff (if they open it) see every batch.
/// </summary>
[ApiController]
[Route("api/v1/dashboard/teacher")]
[Authorize]
public class TeacherDashboardController(ClassKendraContext db, CurrentUser current) : ControllerBase
{
    private const int WatchWindow = 12;   // look at each student's last 12 classes
    private const int WatchMissed = 3;    // flag 3 or more absences in that window
    private const int TrendDays = 30;

    [HttpGet]
    public async Task<ActionResult<TeacherDashboard>> Get(CancellationToken ct)
    {
        var today = Clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var prevMonthStart = monthStart.AddMonths(-1);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
        var from = new[] { prevMonthStart, today.AddDays(-60) }.Min();

        // ---------- My batches and students ----------
        var batchQuery = db.Batches.AsNoTracking();
        if (current.Role == "Teacher")
            batchQuery = batchQuery.Where(b => b.Users.Any(u => u.Id == current.UserId));
        var batches = await batchQuery
            .Select(b => new { b.Id, b.Name, b.Days, b.StartTime, b.EndTime })
            .ToListAsync(ct);
        var batchIds = batches.Select(b => b.Id).ToList();

        var students = await db.Students.AsNoTracking()
            .Where(s => s.IsActive && batchIds.Contains(s.BatchId))
            .Select(s => new { s.Id, s.Name, s.BatchId, s.JoinedOn })
            .ToListAsync(ct);
        var studentsByBatch = students.GroupBy(s => s.BatchId).ToDictionary(g => g.Key, g => g.ToList());

        // ---------- Saved registers (only absentees are stored) ----------
        var sheets = await db.AttendanceSheets.AsNoTracking()
            .Where(a => batchIds.Contains(a.BatchId) && a.Date >= from && a.Date <= today)
            .Select(a => new
            {
                a.BatchId,
                a.Date,
                a.PresentCount,
                a.SavedAt,
                Absences = a.AttendanceAbsences.Select(x => new { x.StudentId, x.AlertedAt }).ToList()
            })
            .ToListAsync(ct);

        static int Pct(int present, int total) => total == 0 ? 100 : (int)Math.Round(present * 100.0 / total);
        int? PeriodPct(DateOnly start, DateOnly end)
        {
            var list = sheets.Where(s => s.Date >= start && s.Date < end).ToList();
            var total = list.Sum(s => s.PresentCount + s.Absences.Count);
            return total == 0 ? null : Pct(list.Sum(s => s.PresentCount), total);
        }

        // ---------- This week ----------
        var weekDays = Enumerable.Range(0, 6).Select(i => weekStart.AddDays(i)).ToList();   // Mon–Sat
        var weekSheets = sheets.Where(s => s.Date >= weekStart).ToList();
        var weekScheduled = batches.Sum(b => weekDays.Count(d => ClassDays.MeetsOn(b.Days, d)));
        var weekMarked = weekSheets.Count(s => ClassDays.MeetsOn(batches.First(b => b.Id == s.BatchId).Days, s.Date));
        var weekStartUtc = weekStart.ToDateTime(TimeOnly.MinValue).AddMinutes(-330);
        var alertsThisWeek = weekSheets.Sum(s => s.Absences.Count(a => a.AlertedAt >= weekStartUtc));

        string? Hm(TimeOnly? t) => t?.ToString("HH:mm");
        string IstClock(DateTime utc) => utc.AddMinutes(330).ToString("HH:mm");

        // ---------- Today ----------
        var classesToday = batches
            .Where(b => ClassDays.MeetsOn(b.Days, today))
            .OrderBy(b => b.StartTime ?? TimeOnly.MaxValue)
            .Select(b =>
            {
                var sheet = sheets.FirstOrDefault(s => s.BatchId == b.Id && s.Date == today);
                var count = studentsByBatch.GetValueOrDefault(b.Id)?.Count ?? 0;
                return new TeacherClassToday(b.Id.ToString(), b.Name, Hm(b.StartTime), Hm(b.EndTime), b.Days, count,
                    sheet is null ? null : Math.Max(0, count - sheet.Absences.Count),
                    sheet is null ? null : IstClock(sheet.SavedAt),
                    sheet?.Absences.Count(a => a.AlertedAt is not null) ?? 0);
            })
            .ToList();

        // ---------- Students to watch: 3+ absences in their last 12 classes ----------
        var sheetsByBatch = sheets.GroupBy(s => s.BatchId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Date).ToList());
        var watch = new List<WatchStudent>();
        foreach (var st in students)
        {
            if (!sheetsByBatch.TryGetValue(st.BatchId, out var list)) continue;
            var recent = list.Where(s => s.Date >= st.JoinedOn).TakeLast(WatchWindow).ToList();
            var history = recent.Select(s => s.Absences.Any(a => a.StudentId == st.Id)).ToList();
            var missed = history.Count(x => x);
            if (missed < WatchMissed) continue;
            var lastAbsent = recent.Last(s => s.Absences.Any(a => a.StudentId == st.Id)).Date;
            watch.Add(new WatchStudent(st.Id.ToString(), st.Name, batches.First(b => b.Id == st.BatchId).Name,
                lastAbsent.ToString("yyyy-MM-dd"), missed, history.Count, history));
        }
        watch = watch.OrderByDescending(w => w.Missed).ThenByDescending(w => w.LastAbsent).Take(6).ToList();

        // ---------- Each batch over the last 30 days ----------
        var trendFrom = today.AddDays(-TrendDays);
        var batchRows = batches
            .OrderBy(b => b.Name)
            .Select(b =>
            {
                var list = sheetsByBatch.GetValueOrDefault(b.Id)?.Where(s => s.Date > trendFrom).ToList() ?? [];
                var classes = list
                    .Select(s => new TeacherBatchClass(s.Date.ToString("yyyy-MM-dd"), Pct(s.PresentCount, s.PresentCount + s.Absences.Count)))
                    .ToList();
                var total = list.Sum(s => s.PresentCount + s.Absences.Count);
                return new TeacherBatch(b.Id.ToString(), b.Name, studentsByBatch.GetValueOrDefault(b.Id)?.Count ?? 0,
                    total == 0 ? null : Pct(list.Sum(s => s.PresentCount), total),
                    classes,
                    classes.Count == 0 ? null : classes.MinBy(c => c.Pct));
            })
            .ToList();

        // ---------- Week grid: one row per batch ----------
        var slots = batches
            .Where(b => weekDays.Any(d => ClassDays.MeetsOn(b.Days, d)))
            .OrderBy(b => b.StartTime ?? TimeOnly.MaxValue).ThenBy(b => b.Name)
            .Select(b => new TeacherWeekSlot(b.Id.ToString(), b.Name, Hm(b.StartTime),
                weekDays.Select(d =>
                {
                    var sheet = sheets.FirstOrDefault(s => s.BatchId == b.Id && s.Date == d);
                    return new TeacherWeekDay(d.ToString("yyyy-MM-dd"), ClassDays.MeetsOn(b.Days, d),
                        sheet?.PresentCount, sheet is null ? studentsByBatch.GetValueOrDefault(b.Id)?.Count ?? 0
                                                           : sheet.PresentCount + sheet.Absences.Count);
                }).ToList()))
            .ToList();

        return new TeacherDashboard(
            batches.Count, students.Count,
            PeriodPct(monthStart, monthStart.AddMonths(1)), PeriodPct(prevMonthStart, monthStart),
            weekMarked, weekScheduled, alertsThisWeek,
            classesToday, watch, batchRows,
            new TeacherWeek(weekStart.ToString("yyyy-MM-dd"), slots));
    }
}
