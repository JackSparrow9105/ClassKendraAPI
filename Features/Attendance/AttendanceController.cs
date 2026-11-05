using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Attendance;

// Matches AttendanceSheet in models.ts.
public record AttendanceSheetDto(string BatchId, string Date, string[] AbsentStudentIds, bool Saved);
public record SaveAttendanceInput(string Date, string[] AbsentStudentIds);
public record SaveAttendanceResult(int Alerted);

/// <summary>
/// Attendance is saved as "who was absent" per batch per day; everyone else is present.
/// Teachers can only see and mark their own batches.
/// </summary>
[ApiController]
[Route("api/v1/attendance/{batchId:guid}")]
[Authorize]
public class AttendanceController(ClassKendraContext db, CurrentUser current, MessageService messages,
                                  ILogger<AttendanceController> log) : ControllerBase
{
    private const int MaxDaysBack = 60;

    /// <summary>GET /attendance/{batchId}?date=yyyy-MM-dd</summary>
    [HttpGet]
    public async Task<ActionResult<AttendanceSheetDto>> Get(Guid batchId, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? Clock.Today;
        if (!await CanUse(batchId, ct)) return NotFound(new { message = "Batch not found." });

        var absent = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.BatchId == batchId && a.Date == day)
            .Select(a => a.AttendanceAbsences.Select(x => x.StudentId).ToList())
            .FirstOrDefaultAsync(ct);

        return new AttendanceSheetDto(batchId.ToString(), day.ToString("yyyy-MM-dd"),
            absent?.Select(x => x.ToString()).ToArray() ?? [], absent is not null);
    }

    /// <summary>
    /// POST /attendance/{batchId} { date, absentStudentIds } → { alerted }
    /// Saving again on the same day only alerts newly absent students. Alerts go out only for today's register.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SaveAttendanceResult>> Save(Guid batchId, SaveAttendanceInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!await CanUse(batchId, ct)) return NotFound(new { message = "Batch not found." });
        if (!DateOnly.TryParse(input.Date, out var day)) return BadRequest(new { message = "Choose a valid date." });

        var today = Clock.Today;
        if (day > today) return BadRequest(new { message = "You can't mark attendance for a future date." });
        if (today.DayNumber - day.DayNumber > MaxDaysBack)
            return BadRequest(new { message = $"Attendance older than {MaxDaysBack} days can't be changed." });

        var students = await db.Students.AsNoTracking()
            .Where(s => s.BatchId == batchId && s.IsActive)
            .Select(s => new { s.Id, s.Name, s.ParentName, s.ParentPhone })
            .ToDictionaryAsync(s => s.Id, ct);

        var absent = (input.AbsentStudentIds ?? [])
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(students.ContainsKey)
            .ToHashSet();

        var sheet = await db.AttendanceSheets.Include(a => a.AttendanceAbsences)
            .FirstOrDefaultAsync(a => a.BatchId == batchId && a.Date == day, ct);
        if (sheet is null)
        {
            sheet = new AttendanceSheet { TenantId = tenantId, BatchId = batchId, Date = day };
            db.AttendanceSheets.Add(sheet);
        }

        // Keep rows (and their "alert sent" time) for students who are still absent.
        foreach (var row in sheet.AttendanceAbsences.Where(a => !absent.Contains(a.StudentId)).ToList())
            sheet.AttendanceAbsences.Remove(row);
        foreach (var id in absent.Where(id => sheet.AttendanceAbsences.All(a => a.StudentId != id)))
            sheet.AttendanceAbsences.Add(new AttendanceAbsence { StudentId = id });

        sheet.PresentCount = students.Count - absent.Count;
        sheet.MarkedByUserId = current.UserId;
        sheet.SavedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Someone else just saved this register. Reload and try again." });
        }

        var alerted = 0;
        if (day == today)
        {
            foreach (var row in sheet.AttendanceAbsences.Where(a => a.AlertedAt is null))
            {
                var st = students[row.StudentId];
                try
                {
                    if (!await messages.QueueAbsentAlertAsync(st.Id, st.Name, st.ParentName, st.ParentPhone, day, ct)) break;   // monthly limit reached
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Absent alert not queued for {Student}", st.Id);
                    break;
                }
                row.AlertedAt = DateTime.UtcNow;
                alerted++;
            }
            if (alerted > 0) await db.SaveChangesAsync(ct);
        }

        return new SaveAttendanceResult(alerted);
    }

    private Task<bool> CanUse(Guid batchId, CancellationToken ct) =>
        current.Role == "Teacher"
            ? db.Batches.AnyAsync(b => b.Id == batchId && b.Users.Any(u => u.Id == current.UserId), ct)
            : db.Batches.AnyAsync(b => b.Id == batchId, ct);
}
