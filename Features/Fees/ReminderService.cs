using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public record RemindResult(int Sent, int Skipped, bool LimitReached);

/// <summary>
/// Fee reminders on WhatsApp — one message per family (same parent phone) with everything the children owe.
/// A phone gets at most one reminder per day.
///
/// Automatic reminders (FeeSettings):
///   RemindBefore — 3 days before the due date
///   RemindOnDue  — on the due date
///   RemindAfter  — 3 days after, then every 7 days, at most 5 times per bill
/// Automatic reminders are not sent on a holiday (Settings › Holidays); they go out on the next working day.
/// Students who left with fees still owed keep getting reminders unless the centre switched that off for them.
/// </summary>
public class ReminderService(ClassKendraContext db, MessageService messages, FeeReadService views,
                             ClassKendraAPI.Features.Holidays.HolidayService holidays)
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

        // No automatic reminders on a holiday. They go out on the next working day instead:
        // that day also sends whatever fell on the holidays just before it.
        if (await holidays.NameAsync(today, ct) is not null) return new(0, 0, false);
        var days = await holidays.HolidaysJustBeforeAsync(today, ct);
        days.Add(today);

        var before = today.AddDays(3);
        var dues = await Unpaid().Where(d => d.DueDate <= before).ToListAsync(ct);

        // A late fee bill never triggers a reminder by itself (it still shows in the amount owed).
        var triggered = dues.Where(d => d.Kind != BillKinds.LateFee && days.Any(day =>
        {
            var late = day.DayNumber - d.DueDate.DayNumber;
            return (s.RemindBefore && late == -3)
                || (s.RemindOnDue && late == 0)
                || (s.RemindAfter && late >= 3 && (late - 3) % 7 == 0 && d.RemindersSent < MaxAfterReminders + 2);
        })).Select(d => d.Id).ToHashSet();

        // Only students with a trigger today; their message still shows the full amount owed.
        var students = dues.Where(d => triggered.Contains(d.Id)).Select(d => d.StudentId).ToHashSet();
        return await SendAsync(dues.Where(d => students.Contains(d.StudentId)).ToList(), triggered, ct);
    }

    /// <summary>
    /// One WhatsApp per FAMILY (same parent phone): "Aarav & Diya's fees of ₹9,000…", with one pay link that shows
    /// every child. Brothers and sisters who owe are added even when only one child was chosen or triggered.
    /// A phone gets at most one reminder per day.
    /// </summary>
    private async Task<RemindResult> SendAsync(List<FeeDue> dues, HashSet<Guid> countAgainst, CancellationToken ct)
    {
        var ids = dues.Select(d => d.StudentId).Distinct().ToList();
        if (ids.Count == 0) return new(0, 0, false);

        var chosen = await db.Students.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && (s.IsActive || s.RemindAfterLeaving))   // left with fees owed: still reminded, unless switched off
            .Select(s => s.ParentPhone).Distinct().ToListAsync(ct);
        if (chosen.Count == 0) return new(0, ids.Count, false);

        // Everyone in those families who can be reminded, with all they owe.
        var members = await db.Students.AsNoTracking()
            .Where(s => chosen.Contains(s.ParentPhone) && (s.IsActive || s.RemindAfterLeaving))
            .Select(s => new { s.Id, s.Name, s.ParentName, s.ParentPhone, s.PayToken, s.JoinedOn })
            .ToListAsync(ct);
        var memberIds = members.Select(m => m.Id).ToList();
        var known = dues.Select(d => d.Id).ToHashSet();
        var more = await Unpaid().Where(d => memberIds.Contains(d.StudentId) && !known.Contains(d.Id)).ToListAsync(ct);
        var allDues = dues.Where(d => memberIds.Contains(d.StudentId)).Concat(more).ToLookup(d => d.StudentId);

        // One reminder per phone per day (IST).
        var dayStartUtc = Clock.Today.ToDateTime(TimeOnly.MinValue) - TimeSpan.FromHours(5.5);
        var alreadyToday = (await db.Messages.AsNoTracking()
                .Where(m => m.Type == MessageTypes.FeeReminder && m.CreatedAt >= dayStartUtc && chosen.Contains(m.Phone))
                .Select(m => m.Phone).ToListAsync(ct))
            .ToHashSet();

        int sent = 0, skipped = 0;
        var families = members.GroupBy(m => m.ParentPhone).ToList();
        foreach (var fam in families)
        {
            if (alreadyToday.Contains(fam.Key)) { skipped++; continue; }

            var owing = fam
                .Select(m => (Member: m, Dues: allDues[m.Id].ToList()))
                .Select(x => (x.Member, x.Dues, Balance: x.Dues.Sum(d => d.GrossAmount - d.Discount - d.PaidAmount)))
                .Where(x => x.Balance > 0)
                .OrderBy(x => x.Member.JoinedOn).ThenBy(x => x.Member.Name)
                .ToList();
            if (owing.Count == 0) { skipped++; continue; }

            var first = owing[0].Member;
            var names = FamilyService.Names(owing.Select(x => x.Member.Name).ToList());
            var balance = owing.Sum(x => x.Balance);
            var oldest = owing.SelectMany(x => x.Dues).Where(d => d.GrossAmount - d.Discount - d.PaidAmount > 0).Min(d => d.DueDate);

            var ok = await messages.QueueFeeReminderAsync(first.Id, names, first.ParentName, first.ParentPhone,
                balance, oldest, views.PayLink(first.PayToken), ct);
            if (!ok)
            {
                skipped += families.Count - sent - skipped;
                return new(sent, skipped, true);
            }

            foreach (var d in owing.SelectMany(x => x.Dues).Where(d => countAgainst.Contains(d.Id))) d.RemindersSent++;
            await db.SaveChangesAsync(ct);
            sent++;
        }
        return new(sent, skipped, false);
    }

    private IQueryable<FeeDue> Unpaid() =>
        db.FeeDues.Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial);
}
