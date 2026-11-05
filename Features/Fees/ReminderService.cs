using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public record RemindResult(int Sent, int Skipped, bool LimitReached);

/// <summary>
/// Fee reminders on WhatsApp — one message per student with everything they owe.
/// A student gets at most one reminder per day.
///
/// Automatic reminders (FeeSettings):
///   RemindBefore — 3 days before the due date
///   RemindOnDue  — on the due date
///   RemindAfter  — 3 days after, then every 7 days, at most 5 times per bill
/// </summary>
public class ReminderService(ClassKendraContext db, MessageService messages, FeeReadService views)
{
    private const int MaxAfterReminders = 5;

    /// <summary>Owner/staff pressed "Send reminder".</summary>
    public async Task<RemindResult> SendManualAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken ct)
    {
        var dues = await Unpaid().Where(d => studentIds.Contains(d.StudentId)).ToListAsync(ct);
        return await SendAsync(dues, dues.Select(d => d.Id).ToHashSet(), ct);
    }

    /// <summary>"Remind all": every family with something unpaid from bills raised up to this month.</summary>
    public async Task<RemindResult> SendAllAsync(string monthKey, CancellationToken ct)
    {
        var dues = await Unpaid().Where(d => string.Compare(d.Month, monthKey) <= 0).ToListAsync(ct);
        var ids = dues.Select(d => d.StudentId).Distinct().ToList();
        return await SendManualAsync(ids, ct);
    }

    /// <summary>Daily job. Safe to run more than once a day.</summary>
    public async Task<RemindResult> SendAutomaticAsync(CancellationToken ct)
    {
        var s = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null || !(s.RemindBefore || s.RemindOnDue || s.RemindAfter)) return new(0, 0, false);

        var today = Clock.Today;
        var before = today.AddDays(3);
        var dues = await Unpaid().Where(d => d.DueDate <= before).ToListAsync(ct);

        var triggered = dues.Where(d =>
        {
            var late = today.DayNumber - d.DueDate.DayNumber;
            return (s.RemindBefore && late == -3)
                || (s.RemindOnDue && late == 0)
                || (s.RemindAfter && late >= 3 && (late - 3) % 7 == 0 && d.RemindersSent < MaxAfterReminders + 2);
        }).Select(d => d.Id).ToHashSet();

        // Only students with a trigger today; their message still shows the full amount owed.
        var students = dues.Where(d => triggered.Contains(d.Id)).Select(d => d.StudentId).ToHashSet();
        return await SendAsync(dues.Where(d => students.Contains(d.StudentId)).ToList(), triggered, ct);
    }

    private async Task<RemindResult> SendAsync(List<FeeDue> dues, HashSet<Guid> countAgainst, CancellationToken ct)
    {
        var byStudent = dues.GroupBy(d => d.StudentId).ToList();
        if (byStudent.Count == 0) return new(0, 0, false);

        var ids = byStudent.Select(g => g.Key).ToList();
        var students = await db.Students.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.IsActive)
            .Select(s => new { s.Id, s.Name, s.ParentName, s.ParentPhone, s.PayToken })
            .ToDictionaryAsync(s => s.Id, ct);

        // One reminder per student per day (IST).
        var dayStartUtc = Clock.Today.ToDateTime(TimeOnly.MinValue) - TimeSpan.FromHours(5.5);
        var alreadyToday = (await db.Messages.AsNoTracking()
                .Where(m => m.Type == MessageTypes.FeeReminder && m.CreatedAt >= dayStartUtc && m.StudentId != null && ids.Contains(m.StudentId.Value))
                .Select(m => m.StudentId!.Value).ToListAsync(ct))
            .ToHashSet();

        int sent = 0, skipped = 0;
        foreach (var g in byStudent)
        {
            if (!students.TryGetValue(g.Key, out var st) || alreadyToday.Contains(g.Key))
            {
                skipped++;
                continue;
            }

            var balance = g.Sum(d => (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount);
            if (balance <= 0) { skipped++; continue; }

            var ok = await messages.QueueFeeReminderAsync(st.Id, st.Name, st.ParentName, st.ParentPhone,
                balance, g.Min(d => d.DueDate), views.PayLink(st.PayToken), ct);
            if (!ok)
            {
                skipped += byStudent.Count - sent - skipped;
                return new(sent, skipped, true);
            }

            foreach (var d in g.Where(d => countAgainst.Contains(d.Id))) d.RemindersSent++;
            await db.SaveChangesAsync(ct);
            sent++;
        }
        return new(sent, skipped, false);
    }

    private IQueryable<FeeDue> Unpaid() =>
        db.FeeDues.Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial);
}
