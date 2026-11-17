using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.StaffAttendance;

// ---- Shapes that match models.ts (staff attendance) ----

/// <summary>Day status: Present, Late, Absent, Leave, Holiday, WeeklyOff, NotYet (today, before the end of school), Upcoming, None (before joining).</summary>
public record StaffDayDto(string Date, string Status, string? CheckInAt, string? CheckOutAt, int LateMinutes, bool MarkedByOwner, string? Note);
public record StaffCountsDto(int Present, int Late, int Leave, int Absent);

/// <summary>
/// Staff attendance for schools: teachers and office staff check in on their phone at school (within the radius),
/// "Late" after start + grace minutes, holidays and weekly offs never count, approved leave counts as Leave,
/// and a working day with no check-in becomes Absent once the school day is over. The owner can mark or correct anyone.
/// </summary>
public class StaffAttendanceService(ClassKendraContext db, CurrentUser current, PlanService plans,
    MessageService messages, PushService push, ILogger<StaffAttendanceService> log)
{
    public static readonly string[] StaffRoles = ["Teacher", "Staff"];
    /// <summary>School plans with staff attendance (School 200 and above). Trials get it too.</summary>
    private static readonly string[] Plans = ["School200", "School300", "School500", "School1000"];

    // ---------- who gets it ----------

    /// <summary>(true, null) or (false, "NotSchool" | "Plan").</summary>
    public async Task<(bool Enabled, string? Reason)> AvailabilityAsync(CancellationToken ct)
    {
        var type = await db.Tenants.Select(t => t.InstitutionType).FirstOrDefaultAsync(ct);
        if (type != "School") return (false, "NotSchool");
        var plan = await plans.GetAsync(ct);
        if (plan is null) return (false, "Plan");
        if (plan.State == PlanStates.Trial) return (true, null);
        // A custom plan for a big school (no student limit, or above 1,000) counts as well.
        var ok = Plans.Contains(plan.PlanCode) || (plan.PlanCode.StartsWith("School") && (plan.MaxStudents is null || plan.MaxStudents >= 200));
        return ok ? (true, null) : (false, "Plan");
    }

    // ---------- settings and calendar ----------

    public async Task<StaffAttendanceSetting> SettingsAsync(CancellationToken ct) =>
        await db.StaffAttendanceSettings.FirstOrDefaultAsync(ct)
        ?? new StaffAttendanceSetting { TenantId = current.TenantId ?? Guid.Empty };

    /// <summary>Settings row to change (created with the defaults the first time).</summary>
    public async Task<StaffAttendanceSetting> SettingsForUpdateAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await db.StaffAttendanceSettings.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new StaffAttendanceSetting { TenantId = tenantId };
        db.StaffAttendanceSettings.Add(s);
        return s;
    }

    public sealed class Calendar(StaffAttendanceSetting settings, List<StaffHoliday> holidays)
    {
        public StaffAttendanceSetting Settings { get; } = settings;
        public List<StaffHoliday> Holidays { get; } = holidays;
        private readonly HashSet<int> _off = ParseOff(settings.WeeklyOff);

        public bool IsWeeklyOff(DateOnly d) => _off.Contains((int)d.DayOfWeek);
        public string? HolidayName(DateOnly d) => Holidays.FirstOrDefault(h => h.FromDate <= d && d <= h.ToDate)?.Name;
        public bool IsWorkingDay(DateOnly d) => !IsWeeklyOff(d) && HolidayName(d) is null;
        public int WorkingDays(DateOnly from, DateOnly to)
        {
            var n = 0;
            for (var d = from; d <= to; d = d.AddDays(1)) if (IsWorkingDay(d)) n++;
            return n;
        }
    }

    public static HashSet<int> ParseOff(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v is >= 0 and <= 6).ToHashSet();

    public async Task<Calendar> CalendarAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var holidays = await db.StaffHolidays.AsNoTracking()
            .Where(h => h.FromDate <= to && h.ToDate >= from).OrderBy(h => h.FromDate).ToListAsync(ct);
        return new Calendar(settings, holidays);
    }

    public static (TimeOnly Start, TimeOnly End) Timing(StaffAttendanceSetting s, TimeOnly? ownStart, TimeOnly? ownEnd) =>
        (ownStart ?? s.StartTime, ownEnd ?? s.EndTime);

    // ---------- day statuses ----------

    public record Person(Guid Id, string Name, string Role, string Phone, DateOnly Joined, TimeOnly Start, TimeOnly End);

    public async Task<List<Person>> PeopleAsync(Calendar cal, Guid? onlyUser, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && StaffRoles.Contains(u.Role) && (onlyUser == null || u.Id == onlyUser))
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name, u.Role, u.Phone, u.CreatedAt, u.StaffStartTime, u.StaffEndTime })
            .ToListAsync(ct);
        return rows.Select(u =>
        {
            var (start, end) = Timing(cal.Settings, u.StaffStartTime, u.StaffEndTime);
            return new Person(u.Id, u.Name, u.Role, u.Phone, DateOnly.FromDateTime(u.CreatedAt + Clock.Ist), start, end);
        }).ToList();
    }

    /// <summary>Every day from..to for these people: records, approved leave, holidays, weekly offs.</summary>
    public async Task<Dictionary<Guid, List<StaffDayDto>>> DaysAsync(Calendar cal, List<Person> people, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = people.Select(p => p.Id).ToList();
        var records = await db.StaffAttendance.AsNoTracking()
            .Where(r => ids.Contains(r.UserId) && r.Date >= from && r.Date <= to).ToListAsync(ct);
        var leaves = await db.StaffLeaves.AsNoTracking()
            .Where(l => ids.Contains(l.UserId) && l.Status == LeaveStatuses.Approved && l.FromDate <= to && l.ToDate >= from)
            .ToListAsync(ct);
        var byKey = records.ToDictionary(r => (r.UserId, r.Date));

        var today = Clock.Today;
        var now = TimeOnly.FromDateTime(Clock.IndiaNow);
        var result = new Dictionary<Guid, List<StaffDayDto>>();
        foreach (var p in people)
        {
            var list = new List<StaffDayDto>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd");
                if (byKey.TryGetValue((p.Id, d), out var r))
                {
                    list.Add(new StaffDayDto(key, r.Status, Iso(r.CheckInAt), Iso(r.CheckOutAt), r.LateMinutes, r.MarkedByUserId != null, null));
                    continue;
                }
                string status; string? note = null;
                if (d < p.Joined) status = "None";
                else if (cal.HolidayName(d) is { } h) { status = "Holiday"; note = h; }
                else if (cal.IsWeeklyOff(d)) status = "WeeklyOff";
                else if (leaves.Any(l => l.UserId == p.Id && l.FromDate <= d && d <= l.ToDate)) status = StaffDayStatuses.Leave;
                else if (d > today) status = "Upcoming";
                else if (d == today && now < p.End) status = "NotYet";
                else status = StaffDayStatuses.Absent;
                list.Add(new StaffDayDto(key, status, null, null, 0, false, note));
            }
            result[p.Id] = list;
        }
        return result;
    }

    public static StaffCountsDto Count(IEnumerable<StaffDayDto> days)
    {
        int present = 0, late = 0, leave = 0, absent = 0;
        foreach (var d in days)
            switch (d.Status)
            {
                case StaffDayStatuses.Present: present++; break;
                case StaffDayStatuses.Late: late++; break;
                case StaffDayStatuses.Leave: leave++; break;
                case StaffDayStatuses.Absent: absent++; break;
            }
        return new StaffCountsDto(present, late, leave, absent);
    }

    // ---------- check in / out ----------

    /// <summary>Metres between two points (haversine).</summary>
    public static double Distance(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371000;
        double Rad(double x) => x * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Minutes after the start time (0 when on time), and whether that is past the grace minutes.</summary>
    public static (int LateMinutes, bool IsLate) Lateness(TimeOnly arrived, TimeOnly start, int grace)
    {
        var minutes = (int)Math.Floor((arrived - start).TotalMinutes);
        if (arrived < start || minutes < 0) return (0, false);
        return (minutes, minutes > grace);
    }

    // ---------- leave ----------

    public static string DatesText(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("ddd d MMM")
        : from.Month == to.Month ? $"{from:ddd d} – {to:ddd d MMM}"
        : $"{from:ddd d MMM} – {to:ddd d MMM}";

    /// <summary>Tells the owner(s) about a new leave request: app notification, and WhatsApp to anyone it didn't reach if turned on.</summary>
    public async Task NotifyLeaveRequestAsync(StaffLeave l, string staffName, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (!s.NotifyLeaveRequests) return;
        var dates = DatesText(l.FromDate, l.ToDate);
        var owners = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == "Owner")
            .Select(u => new { u.Id, u.Name, u.Phone }).ToListAsync(ct);
        foreach (var o in owners)
        {
            try
            {
                var sent = await push.SendAsync(o.Id, $"Leave request from {staffName}",
                    $"{dates} ({l.Days} day{(l.Days == 1 ? "" : "s")}): {l.Reason}", "/staff-attendance/leaves", ct);
                if (sent == 0 && s.LeaveWhatsApp)
                    await messages.QueueLeaveRequestAsync(o.Name, o.Phone, staffName, dates, l.Days, l.Reason, ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "Leave request notice to {Owner} failed", o.Id); }
        }
    }

    /// <summary>Tells the teacher the answer: app notification, and WhatsApp if it didn't reach them and WhatsApp is turned on.</summary>
    public async Task NotifyLeaveAnswerAsync(StaffLeave l, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (!s.NotifyLeaveAnswers) return;
        var u = await db.Users.AsNoTracking().Where(x => x.Id == l.UserId).Select(x => new { x.Name, x.Phone }).FirstOrDefaultAsync(ct);
        if (u is null) return;
        var dates = DatesText(l.FromDate, l.ToDate);
        var approved = l.Status == LeaveStatuses.Approved;
        try
        {
            var sent = await push.SendAsync(l.UserId,
                approved ? "Leave approved" : "Leave not approved",
                approved ? $"Your leave for {dates} is approved." : $"{dates}: {(string.IsNullOrWhiteSpace(l.RejectReason) ? "No reason given" : l.RejectReason)}",
                "/me/leave", ct);
            if (sent == 0 && s.LeaveWhatsApp)
                await messages.QueueLeaveAnswerAsync(u.Name, u.Phone, dates, approved, l.RejectReason, ct);
        }
        catch (Exception ex) { log.LogWarning(ex, "Leave answer notice to {User} failed", l.UserId); }
    }

    public static string? Iso(DateTime? utc) => utc is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o") : null;
}
