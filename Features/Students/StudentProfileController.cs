using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

// Match StudentAttendance and UpcomingFee in models.ts.
public record AttendanceDay(string Date, string Status);
/// <param name="Since">First day counted: the latest of the session start, the joining date and the day they moved to this batch.</param>
/// <param name="Days">Every register of the chosen month for the student's batch: Present or Absent.</param>
/// <param name="RecentAbsences">The last 10 days absent this session, newest first.</param>
public record StudentAttendance(string Since, int Present, int Absent, int? Percent, string Month, List<AttendanceDay> Days, List<string> RecentAbsences);
public record UpcomingFee(string HeadName, string PeriodLabel, string Month, string DueDate, int GrossAmount, int Discount, int Amount);

/// <summary>The student profile's Attendance tab (teachers too, for their own classes) and the fee schedule ahead.</summary>
[ApiController]
[Route("api/v1/students/{id:guid}")]
[Authorize]
public class StudentProfileController(ClassKendraContext db, CurrentUser current, FamilyService family) : ControllerBase
{
    /// <summary>
    /// GET /students/{id}/attendance?month=yyyy-MM → StudentAttendance
    /// Counts the registers of the student's current batch this session (from the day they joined or moved in).
    /// Days with no register (holidays, no class) don't count either way.
    /// </summary>
    [HttpGet("attendance")]
    public async Task<ActionResult<StudentAttendance>> Attendance(Guid id, [FromQuery] string? month, CancellationToken ct)
    {
        var s = await db.Students.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || (current.Role == "Teacher" &&
                          !await db.Batches.AnyAsync(b => b.Id == s.BatchId && b.Users.Any(u => u.Id == current.UserId), ct)))
            return NotFound(new { message = "Student not found." });

        var today = Clock.Today;
        var sessionStartMonth = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var sessionStart = new DateOnly(today.Month >= sessionStartMonth ? today.Year : today.Year - 1, sessionStartMonth, 1);
        var movedIn = await db.StudentBatchHistories.AsNoTracking()
            .Where(h => h.StudentId == id && h.ToBatchId == s.BatchId)
            .MaxAsync(h => (DateTime?)h.ChangedAt, ct);
        var since = new[] { sessionStart, s.JoinedOn, movedIn is DateTime m ? DateOnly.FromDateTime(m.AddMinutes(330)) : DateOnly.MinValue }.Max();
        var until = s.IsActive || s.LeftOn is null ? today : s.LeftOn.Value;

        var sheets = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.BatchId == s.BatchId && a.Date >= since && a.Date <= until)
            .Select(a => new { a.Date, Absent = a.AttendanceAbsences.Any(x => x.StudentId == id) })
            .ToListAsync(ct);

        var monthKey = string.IsNullOrWhiteSpace(month) ? today.ToString("yyyy-MM") : month;
        if (!DateOnly.TryParse(monthKey + "-01", out var monthStart)) return BadRequest(new { message = "Choose a valid month." });
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        // The month view shows every register of that month (even before "since", e.g. last session).
        var monthSheets = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.BatchId == s.BatchId && a.Date >= monthStart && a.Date <= monthEnd && a.Date >= s.JoinedOn)
            .OrderBy(a => a.Date)
            .Select(a => new { a.Date, Absent = a.AttendanceAbsences.Any(x => x.StudentId == id) })
            .ToListAsync(ct);

        var absent = sheets.Count(x => x.Absent);
        var present = sheets.Count - absent;
        return new StudentAttendance(
            since.ToString("yyyy-MM-dd"), present, absent,
            sheets.Count == 0 ? null : (int)Math.Round(100.0 * present / sheets.Count),
            monthKey,
            monthSheets.Select(x => new AttendanceDay(x.Date.ToString("yyyy-MM-dd"), x.Absent ? "Absent" : "Present")).ToList(),
            sheets.Where(x => x.Absent).OrderByDescending(x => x.Date).Take(10).Select(x => x.Date.ToString("yyyy-MM-dd")).ToList());
    }

    /// <summary>
    /// GET /students/{id}/fees/upcoming → UpcomingFee[] — fees that will be raised for the rest of this session
    /// (after this month), with due dates and amounts after discounts, from today's fee setup.
    /// Office only. A change to fees, discounts or the student's batch changes this list.
    /// </summary>
    [HttpGet("fees/upcoming")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<List<UpcomingFee>>> Upcoming(Guid id, CancellationToken ct)
    {
        var s = await db.Students.AsNoTracking().Include(x => x.FeeHeads).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });
        if (!s.IsActive) return new List<UpcomingFee>();

        var settings = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null) return new List<UpcomingFee>();
        var heads = await db.FeeHeads.AsNoTracking().Include(h => h.FeeHeadAmounts)
            .Where(h => !h.IsDeleted && h.Kind == null).ToListAsync(ct);
        var concessions = await db.StudentConcessions.AsNoTracking().Where(c => c.StudentId == id).ToListAsync(ct);
        var siblingConcession = FamilyService.SiblingConcession(settings);
        var isSibling = siblingConcession is not null && (await family.SiblingsWithDiscountAsync([s.ParentPhone], ct)).Contains(id);
        var priced = FamilyService.WithSibling(concessions, isSibling, siblingConcession).ToList();
        var billed = (await db.FeeDues.AsNoTracking().Where(d => d.StudentId == id && d.Status != DueStatus.Cancelled)
                .Select(d => new { d.FeeHeadId, d.Period }).ToListAsync(ct))
            .Select(x => (x.FeeHeadId, x.Period)).ToHashSet();
        var optedIn = s.FeeHeads.Select(h => h.Id).ToHashSet();

        var today = Clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var sessionStart = new DateOnly(today.Month >= settings.SessionStartMonth ? today.Year : today.Year - 1, settings.SessionStartMonth, 1);
        var sessionEnd = sessionStart.AddMonths(12);

        var list = new List<UpcomingFee>();
        for (var month = thisMonth.AddMonths(1); month < sessionEnd; month = month.AddMonths(1))
        {
            foreach (var h in heads)
            {
                var gross = h.FeeHeadAmounts.FirstOrDefault(a => a.BatchId == s.BatchId)?.Amount ?? 0;
                if (gross <= 0 || (h.IsOptional && !optedIn.Contains(h.Id))) continue;
                if (!BillingService.IsDueIn(h.Frequency, month, settings.SessionStartMonth, s.JoinedOn, s.IsNewAdmission)) continue;
                var period = BillingService.PeriodFor(h.Frequency, month, settings.SessionStartMonth);
                if (billed.Contains((h.Id, period.Key))) continue;
                var discount = BillingService.Discount(gross, priced, h.Id);
                if (gross - discount <= 0) continue;
                var due = new DateOnly(month.Year, month.Month, Math.Clamp((int)settings.DueDay, 1, 28));
                list.Add(new UpcomingFee(h.Name, period.Label, month.ToString("yyyy-MM"), due.ToString("yyyy-MM-dd"), gross, discount, gross - discount));
            }
        }
        return list.OrderBy(f => f.DueDate).ThenBy(f => f.HeadName).ToList();
    }
}
