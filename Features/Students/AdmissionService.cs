using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

/// <summary>
/// Admissions: every new student gets an admission number ("A-2026-0045", restarting each session),
/// and the parent gets a WhatsApp admission confirmation with what to pay now.
/// </summary>
public class AdmissionService(ClassKendraContext db, MessageService messages, ILogger<AdmissionService> log)
{
    /// <summary>Gives admission numbers to new (not yet saved) students, in the order given. Safe with two at once.</summary>
    public async Task AssignNumbersAsync(Guid tenantId, IReadOnlyList<Student> students, CancellationToken ct)
    {
        if (students.Count == 0) return;
        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;

        foreach (var group in students.GroupBy(s => (short)FeeViews.SessionYear(s.JoinedOn.ToString("yyyy-MM"), sessionStart)))
        {
            var list = group.ToList();
            var last = await ReserveAsync(tenantId, group.Key, list.Count, ct);
            var first = last - list.Count + 1;
            for (var i = 0; i < list.Count; i++)
                list[i].AdmissionNo = $"A-{group.Key}-{first + i:D4}";
        }
    }

    /// <summary>Reserves "count" numbers for the session and returns the last one.</summary>
    private async Task<int> ReserveAsync(Guid tenantId, short year, int count, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>($"""
            DECLARE @n int;
            UPDATE AdmissionSequences WITH (UPDLOCK, HOLDLOCK)
               SET @n = LastNumber = LastNumber + {count}
             WHERE TenantId = {tenantId} AND SessionYear = {year};
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO AdmissionSequences (TenantId, SessionYear, LastNumber) VALUES ({tenantId}, {year}, {count});
                SET @n = {count};
            END
            SELECT @n AS [Value];
            """).ToListAsync(ct)).Single();

    /// <summary>
    /// WhatsApp admission confirmation for a saved student (after this month's bills are raised).
    /// force = send even when the centre turned admission messages off (the "send again" button).
    /// False when skipped, the student has no admission number, or the monthly message limit is used up.
    /// </summary>
    public async Task<bool> SendConfirmationAsync(Guid studentId, bool force, CancellationToken ct)
    {
        var s = await db.Students.AsNoTracking()
            .Where(x => x.Id == studentId)
            .Select(x => new { x.Id, x.Name, x.ParentName, x.ParentPhone, x.AdmissionNo, Batch = x.Batch.Name })
            .FirstOrDefaultAsync(ct);
        if (s is null || string.IsNullOrEmpty(s.AdmissionNo)) return false;

        var dues = await db.FeeDues.AsNoTracking()
            .Where(d => d.StudentId == studentId && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial))
            .Select(d => new { Balance = (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount, d.DueDate })
            .ToListAsync(ct);
        var amount = dues.Sum(d => d.Balance);
        DateOnly? due = dues.Count > 0 ? dues.Min(d => d.DueDate) : null;

        try
        {
            var fees = await messages.FeesDueTextAsync(amount, due, ct);
            return await messages.QueueAdmissionAsync(s.Id, s.Name, s.ParentName, s.ParentPhone, s.Batch, s.AdmissionNo, fees, force, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Admission message not queued for {Student}", studentId);
            return false;
        }
    }
}
