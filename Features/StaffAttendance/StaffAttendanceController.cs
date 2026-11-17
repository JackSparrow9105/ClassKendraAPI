using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.StaffAttendance;

// ---- Shapes that match models.ts ----
public record StaffAvailabilityDto(bool Enabled, string? Reason);
public record MyTodayDto(
    string Date, string Start, string End, string LateAfter, string Status, string? HolidayName,
    string? CheckInAt, string? CheckOutAt, int LateMinutes, bool MarkedByOwner,
    double? SchoolLat, double? SchoolLng, int Radius, StaffCountsDto Month);
public record StaffMonthDto(string Month, StaffDayDto[] Days, StaffCountsDto Counts);
public record CheckInInput(double Latitude, double Longitude, double? Accuracy);
public record LeaveDto(string Id, string UserId, string Name, string Role, string From, string To, int Days, string Reason,
    string Status, string? RejectReason, string CreatedAt, string? DecidedAt, int? TakenThisSession);
public record LeaveInput(string From, string To, string Reason);
public record RejectInput(string? Reason);
public record StaffTodayRowDto(string UserId, string Name, string Role, string Status, string? CheckInAt, string? CheckOutAt,
    int LateMinutes, bool MarkedByOwner, string Start, string End);
public record StaffTodayDto(string Date, string DayType, string? HolidayName, string Start, string End, StaffTodayRowDto[] Rows);
public record MarkInput(string UserId, string Date, string Status, string? CheckIn);
public record StaffReportRowDto(string UserId, string Name, string Role, StaffCountsDto Counts, StaffDayDto[] Days);
public record StaffReportDto(string Month, string From, string To, int WorkingDays, int Holidays, StaffReportRowDto[] Rows);
public record HolidayDto(string Id, string Name, string From, string To);
public record TimingDto(string UserId, string Name, string Role, string? Start, string? End);
public record StaffSettingsDto(string Start, string End, int GraceMinutes, double? Latitude, double? Longitude, int Radius,
    int[] WeeklyOff, HolidayDto[] Holidays, TimingDto[] Staff,
    bool NotifyLeaveRequests, bool NotifyLeaveAnswers, bool LeaveWhatsApp);
public record TimingSettingsInput(string Start, string End, int[] WeeklyOff);
public record LocationInput(double Latitude, double Longitude);
public record HolidayInput(string Name, string From, string To);
public record PersonTimingInput(string? Start, string? End);
public record LeaveNotificationsInput(bool NotifyLeaveRequests, bool NotifyLeaveAnswers, bool LeaveWhatsApp);
public record PushSubscriptionInput(string Endpoint, PushKeys Keys);
public record PushKeys(string P256dh, string Auth);

/// <summary>Staff attendance for schools (School 200 and above): check-in, leave, owner's view, report, settings.</summary>
[ApiController]
[Route("api/v1/staff-attendance")]
[Authorize]
public class StaffAttendanceController(ClassKendraContext db, CurrentUser current, StaffAttendanceService svc) : ControllerBase
{
    private const int MaxDaysBack = 60;

    /// <summary>GET /staff-attendance/status → { enabled, reason: "NotSchool" | "Plan" | null }</summary>
    [HttpGet("status")]
    public async Task<StaffAvailabilityDto> Status(CancellationToken ct)
    {
        var (enabled, reason) = await svc.AvailabilityAsync(ct);
        return new StaffAvailabilityDto(enabled, reason);
    }

    // ================= Teacher / office staff =================

    /// <summary>GET /staff-attendance/me/today — the check-in card.</summary>
    [HttpGet("me/today")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<MyTodayDto>> MyToday(CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var today = Clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var cal = await svc.CalendarAsync(monthStart, today, ct);
        var people = await svc.PeopleAsync(cal, current.UserId, ct);
        if (people.Count == 0) return NotFound(new { message = "Staff attendance is for teachers and office staff." });
        var me = people[0];
        var days = (await svc.DaysAsync(cal, people, monthStart, today, ct))[me.Id];
        var t = days[^1];
        var s = cal.Settings;
        return new MyTodayDto(t.Date, Hm(me.Start), Hm(me.End), Hm(me.Start.AddMinutes(s.GraceMinutes)), t.Status, t.Note,
            t.CheckInAt, t.CheckOutAt, t.LateMinutes, t.MarkedByOwner,
            (double?)s.Latitude, (double?)s.Longitude, s.RadiusMeters, StaffAttendanceService.Count(days));
    }

    /// <summary>POST /staff-attendance/me/check-in { latitude, longitude, accuracy } — only within the radius of the school.</summary>
    [HttpPost("me/check-in")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<MyTodayDto>> CheckIn(CheckInInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var today = Clock.Today;
        var cal = await svc.CalendarAsync(today, today, ct);
        var s = cal.Settings;
        if (cal.HolidayName(today) is { } h) return BadRequest(new { message = $"Today is a holiday ({h})." });
        if (cal.IsWeeklyOff(today)) return BadRequest(new { message = "Today is your weekly off." });
        if (s.Latitude is null || s.Longitude is null)
            return BadRequest(new { message = "The principal hasn't set the school location yet. Ask them to set it in Staff attendance settings." });
        if (input.Latitude is < -90 or > 90 || input.Longitude is < -180 or > 180)
            return BadRequest(new { message = "Couldn't read your location. Turn on location and try again." });

        var distance = StaffAttendanceService.Distance(input.Latitude, input.Longitude, (double)s.Latitude, (double)s.Longitude);
        // Phone GPS can be off by a few tens of metres: allow up to 50 m of the reported accuracy.
        var slack = Math.Clamp(input.Accuracy ?? 0, 0, 50);
        if (distance - slack > s.RadiusMeters)
            return BadRequest(new { message = $"You're {Math.Round(distance / 10) * 10:0} m from school. Check in when you reach.", distance = (int)distance });

        var me = (await svc.PeopleAsync(cal, current.UserId, ct)).FirstOrDefault();
        if (me is null) return NotFound(new { message = "Staff attendance is for teachers and office staff." });

        var record = await db.StaffAttendance.FirstOrDefaultAsync(r => r.UserId == me.Id && r.Date == today, ct);
        if (record?.CheckInAt is null)
        {
            var nowUtc = DateTime.UtcNow;
            var (lateMinutes, isLate) = StaffAttendanceService.Lateness(TimeOnly.FromDateTime(Clock.IndiaNow), me.Start, s.GraceMinutes);
            if (record is null)
            {
                record = new StaffAttendanceRecord { TenantId = tenantId, UserId = me.Id, Date = today };
                db.StaffAttendance.Add(record);
            }
            record.CheckInAt = nowUtc;
            record.Status = isLate ? StaffDayStatuses.Late : StaffDayStatuses.Present;
            record.LateMinutes = lateMinutes;
            record.DistanceMeters = (int)distance;
            record.MarkedByUserId = null;
            record.UpdatedAt = nowUtc;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* checked in twice at the same moment: the first one stands */ }
        }
        return await MyToday(ct);
    }

    /// <summary>POST /staff-attendance/me/check-out — records the time (not judged).</summary>
    [HttpPost("me/check-out")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<MyTodayDto>> CheckOut(CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var today = Clock.Today;
        var record = await db.StaffAttendance.FirstOrDefaultAsync(r => r.UserId == current.UserId && r.Date == today, ct);
        if (record?.CheckInAt is null) return BadRequest(new { message = "Check in first." });
        record.CheckOutAt = DateTime.UtcNow;
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await MyToday(ct);
    }

    /// <summary>GET /staff-attendance/me/month?month=2026-11 — my calendar.</summary>
    [HttpGet("me/month")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<StaffMonthDto>> MyMonth([FromQuery] string? month, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var (from, to) = MonthRange(month);
        var cal = await svc.CalendarAsync(from, to, ct);
        var people = await svc.PeopleAsync(cal, current.UserId, ct);
        if (people.Count == 0) return NotFound(new { message = "Staff attendance is for teachers and office staff." });
        var days = (await svc.DaysAsync(cal, people, from, to, ct))[people[0].Id];
        return new StaffMonthDto(from.ToString("yyyy-MM"), days.ToArray(), StaffAttendanceService.Count(days));
    }

    /// <summary>GET /staff-attendance/me/leaves — my leave requests, newest first.</summary>
    [HttpGet("me/leaves")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<LeaveDto[]>> MyLeaves(CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        return await LeaveList(db.StaffLeaves.Where(l => l.UserId == current.UserId), false, ct);
    }

    /// <summary>POST /staff-attendance/me/leaves { from, to, reason } — the principal is notified.</summary>
    [HttpPost("me/leaves")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<ActionResult<LeaveDto>> ApplyLeave(LeaveInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!DateOnly.TryParse(input.From, out var from) || !DateOnly.TryParse(input.To, out var to))
            return BadRequest(new { message = "Choose the dates." });
        if (to < from) return BadRequest(new { message = "The last day can't be before the first day." });
        if (from < Clock.Today.AddDays(-7)) return BadRequest(new { message = "Leave can be applied up to 7 days back." });
        if (to.DayNumber - from.DayNumber > 60) return BadRequest(new { message = "Apply for at most 60 days at a time." });
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length == 0) return BadRequest(new { message = "Write a reason." });
        if (reason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 characters." });

        var cal = await svc.CalendarAsync(from, to, ct);
        var days = cal.WorkingDays(from, to);
        if (days == 0) return BadRequest(new { message = "These dates are all holidays or weekly offs." });
        var overlap = await db.StaffLeaves.AnyAsync(l => l.UserId == current.UserId && l.Status != LeaveStatuses.Rejected
                                                         && l.FromDate <= to && l.ToDate >= from, ct);
        if (overlap) return BadRequest(new { message = "You already applied for leave on some of these days." });

        var leave = new StaffLeave { TenantId = tenantId, UserId = current.UserId, FromDate = from, ToDate = to, Days = days, Reason = reason };
        db.StaffLeaves.Add(leave);
        await db.SaveChangesAsync(ct);

        var name = await db.Users.Where(u => u.Id == current.UserId).Select(u => u.Name).FirstAsync(ct);
        await svc.NotifyLeaveRequestAsync(leave, name, ct);
        return (await LeaveList(db.StaffLeaves.Where(l => l.Id == leave.Id), false, ct))[0];
    }

    /// <summary>DELETE /staff-attendance/me/leaves/{id} — withdraw a request that is still pending.</summary>
    [HttpDelete("me/leaves/{id:guid}")]
    [Authorize(Roles = "Teacher,Staff")]
    public async Task<IActionResult> WithdrawLeave(Guid id, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var n = await db.StaffLeaves.Where(l => l.Id == id && l.UserId == current.UserId && l.Status == LeaveStatuses.Pending)
            .ExecuteDeleteAsync(ct);
        return n == 0 ? BadRequest(new { message = "Only a pending request can be withdrawn." }) : NoContent();
    }

    // ================= Owner =================

    /// <summary>GET /staff-attendance/today?date=yyyy-MM-dd — everyone's status for the day.</summary>
    [HttpGet("today")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffTodayDto>> Today([FromQuery] string? date, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var day = DateOnly.TryParse(date, out var d) ? d : Clock.Today;
        var cal = await svc.CalendarAsync(day, day, ct);
        var people = await svc.PeopleAsync(cal, null, ct);
        var days = await svc.DaysAsync(cal, people, day, day, ct);
        var rows = people.Select(p =>
        {
            var x = days[p.Id][0];
            return new StaffTodayRowDto(p.Id.ToString(), p.Name, p.Role, x.Status, x.CheckInAt, x.CheckOutAt, x.LateMinutes, x.MarkedByOwner, Hm(p.Start), Hm(p.End));
        }).ToArray();
        var dayType = cal.HolidayName(day) is not null ? "Holiday" : cal.IsWeeklyOff(day) ? "WeeklyOff" : "Working";
        return new StaffTodayDto(day.ToString("yyyy-MM-dd"), dayType, cal.HolidayName(day), Hm(cal.Settings.StartTime), Hm(cal.Settings.EndTime), rows);
    }

    /// <summary>
    /// PUT /staff-attendance/mark { userId, date, status: Present|Late|Absent|Leave|Clear, checkIn: "08:05" }
    /// The owner marks or corrects a day; it shows as "marked by owner".
    /// </summary>
    [HttpPut("mark")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffTodayRowDto>> Mark(MarkInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!Guid.TryParse(input.UserId, out var userId) || !DateOnly.TryParse(input.Date, out var day))
            return BadRequest(new { message = "Choose a person and a date." });
        if (day > Clock.Today) return BadRequest(new { message = "You can't mark a future date." });
        if (Clock.Today.DayNumber - day.DayNumber > MaxDaysBack) return BadRequest(new { message = $"Only the last {MaxDaysBack} days can be changed." });
        string[] allowed = [StaffDayStatuses.Present, StaffDayStatuses.Late, StaffDayStatuses.Absent, StaffDayStatuses.Leave, "Clear"];
        if (!allowed.Contains(input.Status)) return BadRequest(new { message = "Choose Present, Late, Absent or Leave." });

        var cal = await svc.CalendarAsync(day, day, ct);
        var person = (await svc.PeopleAsync(cal, userId, ct)).FirstOrDefault();
        if (person is null) return NotFound(new { message = "Teacher or staff member not found." });

        var record = await db.StaffAttendance.FirstOrDefaultAsync(r => r.UserId == userId && r.Date == day, ct);
        if (input.Status == "Clear")
        {
            if (record is not null) { db.StaffAttendance.Remove(record); await db.SaveChangesAsync(ct); }
        }
        else
        {
            if (record is null)
            {
                record = new StaffAttendanceRecord { TenantId = tenantId, UserId = userId, Date = day };
                db.StaffAttendance.Add(record);
            }
            record.Status = input.Status;
            record.MarkedByUserId = current.UserId;
            record.UpdatedAt = DateTime.UtcNow;
            record.LateMinutes = 0;
            if (input.Status is StaffDayStatuses.Present or StaffDayStatuses.Late)
            {
                if (!string.IsNullOrWhiteSpace(input.CheckIn))
                {
                    if (!TimeOnly.TryParse(input.CheckIn, out var at)) return BadRequest(new { message = "Enter the time like 8:05 am." });
                    record.CheckInAt = DateTime.SpecifyKind(day.ToDateTime(at) - Clock.Ist, DateTimeKind.Utc);
                    record.LateMinutes = StaffAttendanceService.Lateness(at, person.Start, cal.Settings.GraceMinutes).LateMinutes;
                }
            }
            else
            {
                record.CheckInAt = null;
                record.CheckOutAt = null;
                record.DistanceMeters = null;
            }
            await db.SaveChangesAsync(ct);
        }

        var x = (await svc.DaysAsync(cal, [person], day, day, ct))[userId][0];
        return new StaffTodayRowDto(userId.ToString(), person.Name, person.Role, x.Status, x.CheckInAt, x.CheckOutAt, x.LateMinutes, x.MarkedByOwner, Hm(person.Start), Hm(person.End));
    }

    /// <summary>GET /staff-attendance/leaves?status=Pending — leave requests (pending first, then the last 60 days decided).</summary>
    [HttpGet("leaves")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<LeaveDto[]>> Leaves(CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var since = DateTime.UtcNow.AddDays(-60);
        return await LeaveList(db.StaffLeaves.Where(l => l.Status == LeaveStatuses.Pending || l.CreatedAt >= since), true, ct);
    }

    [HttpPost("leaves/{id:guid}/approve")]
    [Authorize(Roles = "Owner")]
    public Task<ActionResult<LeaveDto>> Approve(Guid id, CancellationToken ct) => Decide(id, true, null, ct);

    [HttpPost("leaves/{id:guid}/reject")]
    [Authorize(Roles = "Owner")]
    public Task<ActionResult<LeaveDto>> Reject(Guid id, RejectInput input, CancellationToken ct) => Decide(id, false, input.Reason, ct);

    private async Task<ActionResult<LeaveDto>> Decide(Guid id, bool approve, string? reason, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var leave = await db.StaffLeaves.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (leave is null) return NotFound(new { message = "Leave request not found." });
        if (leave.Status != LeaveStatuses.Pending) return BadRequest(new { message = $"This request is already {leave.Status.ToLowerInvariant()}." });
        var why = reason?.Trim();
        if (why is { Length: > 200 }) return BadRequest(new { message = "Keep the reason under 200 characters." });
        leave.Status = approve ? LeaveStatuses.Approved : LeaveStatuses.Rejected;
        leave.RejectReason = approve ? null : (string.IsNullOrEmpty(why) ? null : why);
        leave.DecidedAt = DateTime.UtcNow;
        leave.DecidedByUserId = current.UserId;
        await db.SaveChangesAsync(ct);
        await svc.NotifyLeaveAnswerAsync(leave, ct);
        return (await LeaveList(db.StaffLeaves.Where(l => l.Id == id), true, ct))[0];
    }

    /// <summary>GET /staff-attendance/report?month=2026-11 — per person totals and every day (for Excel).</summary>
    [HttpGet("report")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffReportDto>> Report([FromQuery] string? month, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var (from, to) = MonthRange(month);
        var last = to > Clock.Today ? Clock.Today : to;   // the current month: up to today
        if (last < from) last = from;
        var cal = await svc.CalendarAsync(from, to, ct);
        var people = await svc.PeopleAsync(cal, null, ct);
        var days = await svc.DaysAsync(cal, people, from, to, ct);
        var holidays = 0;
        for (var d = from; d <= last; d = d.AddDays(1)) if (cal.HolidayName(d) is not null) holidays++;
        var rows = people.Select(p => new StaffReportRowDto(p.Id.ToString(), p.Name, p.Role,
            StaffAttendanceService.Count(days[p.Id]), days[p.Id].ToArray())).ToArray();
        return new StaffReportDto(from.ToString("yyyy-MM"), from.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd"),
            cal.WorkingDays(from, last), holidays, rows);
    }

    // ---------- settings ----------

    [HttpGet("settings")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> GetSettings(CancellationToken ct)
    {
        if (await Blocked(ct, settingsOnly: true) is { } blocked) return blocked;
        return await SettingsDto(ct);
    }

    /// <summary>PUT /staff-attendance/settings { start: "07:30", end: "13:30", weeklyOff: [0] }</summary>
    [HttpPut("settings")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> SaveTiming(TimingSettingsInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!TimeOnly.TryParse(input.Start, out var start) || !TimeOnly.TryParse(input.End, out var end))
            return BadRequest(new { message = "Enter the school timing." });
        if (end <= start) return BadRequest(new { message = "School must end after it starts." });
        var off = (input.WeeklyOff ?? []).Where(x => x is >= 0 and <= 6).Distinct().Order().ToArray();
        if (off.Length > 3) return BadRequest(new { message = "Choose at most 3 weekly off days." });
        var s = await svc.SettingsForUpdateAsync(tenantId, ct);
        s.StartTime = start;
        s.EndTime = end;
        s.WeeklyOff = string.Join(',', off);
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await SettingsDto(ct);
    }

    /// <summary>PUT /staff-attendance/settings/location { latitude, longitude } — "Use where I am now", at school.</summary>
    [HttpPut("settings/location")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> SaveLocation(LocationInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (input.Latitude is < -90 or > 90 || input.Longitude is < -180 or > 180 || (input.Latitude == 0 && input.Longitude == 0))
            return BadRequest(new { message = "Couldn't read your location. Turn on location and try again." });
        var s = await svc.SettingsForUpdateAsync(tenantId, ct);
        s.Latitude = Math.Round((decimal)input.Latitude, 6);
        s.Longitude = Math.Round((decimal)input.Longitude, 6);
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await SettingsDto(ct);
    }

    /// <summary>PUT /staff-attendance/settings/notifications { notifyLeaveRequests, notifyLeaveAnswers, leaveWhatsApp }</summary>
    [HttpPut("settings/notifications")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> SaveNotifications(LeaveNotificationsInput input, CancellationToken ct)
    {
        if (await Blocked(ct, settingsOnly: true) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var s = await svc.SettingsForUpdateAsync(tenantId, ct);
        s.NotifyLeaveRequests = input.NotifyLeaveRequests;
        s.NotifyLeaveAnswers = input.NotifyLeaveAnswers;
        s.LeaveWhatsApp = input.LeaveWhatsApp;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await SettingsDto(ct);
    }

    [HttpPost("holidays")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> AddHoliday(HolidayInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 60) return BadRequest(new { message = "Give the holiday a name (up to 60 letters)." });
        if (!DateOnly.TryParse(input.From, out var from)) return BadRequest(new { message = "Choose the date." });
        var to = DateOnly.TryParse(input.To, out var t) ? t : from;
        if (to < from) return BadRequest(new { message = "The last day can't be before the first day." });
        if (to.DayNumber - from.DayNumber > 92) return BadRequest(new { message = "A holiday can be at most 3 months long." });
        db.StaffHolidays.Add(new StaffHoliday { TenantId = tenantId, Name = name, FromDate = from, ToDate = to });
        await db.SaveChangesAsync(ct);
        return await SettingsDto(ct);
    }

    [HttpDelete("holidays/{id:guid}")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> DeleteHoliday(Guid id, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        await db.StaffHolidays.Where(h => h.Id == id).ExecuteDeleteAsync(ct);
        return await SettingsDto(ct);
    }

    /// <summary>PUT /staff-attendance/timings/{userId} { start, end } — a different timing for one person; nulls = the school's.</summary>
    [HttpPut("timings/{userId:guid}")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<StaffSettingsDto>> SaveTiming(Guid userId, PersonTimingInput input, CancellationToken ct)
    {
        if (await Blocked(ct) is { } blocked) return blocked;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && StaffAttendanceService.StaffRoles.Contains(u.Role), ct);
        if (user is null) return NotFound(new { message = "Teacher or staff member not found." });
        if (string.IsNullOrWhiteSpace(input.Start) && string.IsNullOrWhiteSpace(input.End))
        {
            user.StaffStartTime = null;
            user.StaffEndTime = null;
        }
        else
        {
            if (!TimeOnly.TryParse(input.Start, out var start) || !TimeOnly.TryParse(input.End, out var end))
                return BadRequest(new { message = "Enter both times." });
            if (end <= start) return BadRequest(new { message = "The end time must be after the start time." });
            user.StaffStartTime = start;
            user.StaffEndTime = end;
        }
        await db.SaveChangesAsync(ct);
        return await SettingsDto(ct);
    }

    // ---------- helpers ----------

    /// <summary>403 when the centre doesn't have staff attendance (not a school, or a plan below School 200).</summary>
    private async Task<ActionResult?> Blocked(CancellationToken ct, bool settingsOnly = false)
    {
        var (enabled, reason) = await svc.AvailabilityAsync(ct);
        if (enabled) return null;
        if (settingsOnly && reason == "Plan") return null;   // the owner can still see/change notification switches
        return StatusCode(403, new
        {
            message = reason == "NotSchool" ? "Staff attendance is for schools." : "Staff attendance comes with School 200 and above. Upgrade to use it.",
            reason
        });
    }

    private async Task<StaffSettingsDto> SettingsDto(CancellationToken ct)
    {
        var s = await svc.SettingsAsync(ct);
        var holidays = await db.StaffHolidays.AsNoTracking().Where(h => h.ToDate >= Clock.Today.AddDays(-31))
            .OrderBy(h => h.FromDate).ToListAsync(ct);
        var staff = await db.Users.AsNoTracking().Where(u => u.IsActive && StaffAttendanceService.StaffRoles.Contains(u.Role))
            .OrderBy(u => u.Name).Select(u => new { u.Id, u.Name, u.Role, u.StaffStartTime, u.StaffEndTime }).ToListAsync(ct);
        return new StaffSettingsDto(Hm(s.StartTime), Hm(s.EndTime), s.GraceMinutes, (double?)s.Latitude, (double?)s.Longitude, s.RadiusMeters,
            StaffAttendanceService.ParseOff(s.WeeklyOff).Order().ToArray(),
            holidays.Select(h => new HolidayDto(h.Id.ToString(), h.Name, h.FromDate.ToString("yyyy-MM-dd"), h.ToDate.ToString("yyyy-MM-dd"))).ToArray(),
            staff.Select(u => new TimingDto(u.Id.ToString(), u.Name, u.Role,
                u.StaffStartTime is TimeOnly a ? Hm(a) : null, u.StaffEndTime is TimeOnly b ? Hm(b) : null)).ToArray(),
            s.NotifyLeaveRequests, s.NotifyLeaveAnswers, s.LeaveWhatsApp);
    }

    private async Task<LeaveDto[]> LeaveList(IQueryable<StaffLeave> q, bool withTaken, CancellationToken ct)
    {
        var rows = await q.AsNoTracking()
            .OrderByDescending(l => l.Status == LeaveStatuses.Pending).ThenByDescending(l => l.CreatedAt)
            .Select(l => new { l, l.User.Name, l.User.Role }).ToListAsync(ct);

        Dictionary<Guid, int> taken = [];
        if (withTaken && rows.Count > 0)
        {
            // Approved leave days this session (April–March unless the centre set another start).
            var startMonth = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
            var today = Clock.Today;
            var y = today.Month >= startMonth ? today.Year : today.Year - 1;
            var sessionStart = new DateOnly(y, startMonth, 1);
            var ids = rows.Select(r => r.l.UserId).Distinct().ToList();
            taken = await db.StaffLeaves.AsNoTracking()
                .Where(l => ids.Contains(l.UserId) && l.Status == LeaveStatuses.Approved && l.FromDate >= sessionStart)
                .GroupBy(l => l.UserId).Select(g => new { g.Key, Days = g.Sum(x => x.Days) })
                .ToDictionaryAsync(x => x.Key, x => x.Days, ct);
        }
        return rows.Select(r => new LeaveDto(r.l.Id.ToString(), r.l.UserId.ToString(), r.Name, r.Role,
            r.l.FromDate.ToString("yyyy-MM-dd"), r.l.ToDate.ToString("yyyy-MM-dd"), r.l.Days, r.l.Reason, r.l.Status, r.l.RejectReason,
            StaffAttendanceService.Iso(r.l.CreatedAt)!, StaffAttendanceService.Iso(r.l.DecidedAt),
            withTaken ? taken.GetValueOrDefault(r.l.UserId) : null)).ToArray();
    }

    private static (DateOnly From, DateOnly To) MonthRange(string? month)
    {
        var today = Clock.Today;
        var from = DateOnly.TryParseExact((month ?? "") + "-01", "yyyy-MM-dd", out var m) ? m : new DateOnly(today.Year, today.Month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    private static string Hm(TimeOnly t) => t.ToString("HH:mm");
}

/// <summary>App notifications: the public key, and saving / removing this phone's subscription.</summary>
[ApiController]
[Route("api/v1/push")]
[Authorize]
public class PushController(CurrentUser current, PushService push) : ControllerBase
{
    /// <summary>GET /push/key → { publicKey } (null when app notifications are not set up on the server).</summary>
    [HttpGet("key")]
    public IActionResult Key() => Ok(new { publicKey = push.PublicKey });

    /// <summary>POST /push/subscriptions { endpoint, keys: { p256dh, auth } } — this phone allowed notifications.</summary>
    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe(PushSubscriptionInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (string.IsNullOrWhiteSpace(input.Endpoint) || input.Endpoint.Length > 600 || !input.Endpoint.StartsWith("https://")
            || string.IsNullOrWhiteSpace(input.Keys?.P256dh) || string.IsNullOrWhiteSpace(input.Keys?.Auth)
            || input.Keys.P256dh.Length > 200 || input.Keys.Auth.Length > 100)
            return BadRequest(new { message = "This browser's notification details look wrong." });
        await push.SaveAsync(tenantId, current.UserId, input.Endpoint, input.Keys.P256dh, input.Keys.Auth, ct);
        return NoContent();
    }

    /// <summary>DELETE /push/subscriptions?endpoint=… — this phone turned notifications off (or signed out).</summary>
    [HttpDelete("subscriptions")]
    public async Task<IActionResult> Unsubscribe([FromQuery] string endpoint, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) await push.RemoveAsync(endpoint, ct);
        return NoContent();
    }
}
