using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

/// <summary>Numbering settings for Settings → Admissions. NextNumber is the number the next new student gets.</summary>
public record AdmissionNumberingDto(string Style, int NextNumber, string Example);

/// <summary>
/// Admissions: every student gets an admission number — typed in from the school's register, or made automatically
/// ("A-2026-0045" restarting each session, or "1251, 1252…" continuing the register). New admissions also get the
/// admission fee and a WhatsApp admission confirmation; existing students get neither.
/// </summary>
public class AdmissionService(ClassKendraContext db, MessageService messages, ILogger<AdmissionService> log)
{
    public const int MaxLength = 20;

    /// <summary>Gives automatic numbers to (not yet saved) students that have none, in the order given. Safe with two at once.</summary>
    public async Task AssignNumbersAsync(Guid tenantId, IReadOnlyList<Student> students, CancellationToken ct)
    {
        var pending = students.Where(s => string.IsNullOrWhiteSpace(s.AdmissionNo)).ToList();
        if (pending.Count == 0) return;

        var style = await StyleAsync(ct);
        var sessionStart = await SessionStartAsync(ct);
        if (style == AdmissionNoStyles.Serial) await EnsureSerialStartAsync(tenantId, ct);
        var typed = students.Where(s => !string.IsNullOrWhiteSpace(s.AdmissionNo)).Select(s => s.AdmissionNo!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in pending.GroupBy(s => KeyFor(style, s.JoinedOn, sessionStart)))
        {
            var queue = new Queue<Student>(group);
            // A number someone typed by hand may already be taken: skip it and take the next one.
            for (var round = 0; queue.Count > 0 && round < 50; round++)
            {
                var last = await ReserveAsync(tenantId, group.Key, queue.Count, ct);
                var candidates = Enumerable.Range(last - queue.Count + 1, queue.Count).Select(n => Format(style, group.Key, n)).ToList();
                var taken = (await db.Students.IgnoreQueryFilters()
                        .Where(s => s.TenantId == tenantId && s.AdmissionNo != null && candidates.Contains(s.AdmissionNo))
                        .Select(s => s.AdmissionNo!).ToListAsync(ct))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var no in candidates)
                {
                    if (taken.Contains(no) || !typed.Add(no)) continue;
                    queue.Dequeue().AdmissionNo = no;
                    if (queue.Count == 0) break;
                }
            }
            if (queue.Count > 0) throw new InvalidOperationException("Couldn't find a free admission number.");
        }
    }

    /// <summary>
    /// Checks a typed admission number: trimmed, at most 20 letters/digits/-/., not given to another student of this centre.
    /// Returns the clean number, or an error to show.
    /// </summary>
    public async Task<(string? Clean, string? Error)> CheckTypedAsync(string? raw, Guid? studentId, CancellationToken ct)
    {
        var no = (raw ?? "").Trim();
        if (no.Length == 0) return (null, null);
        if (no.Length > MaxLength) return (null, $"Admission no. can be at most {MaxLength} characters.");
        if (!no.All(c => char.IsLetterOrDigit(c) || c is '-' or '/' or '.' or ' '))
            return (null, "Admission no. can have letters, numbers and - / . only.");

        var other = await db.Students
            .Where(s => s.AdmissionNo == no && s.Id != studentId)
            .Select(s => s.Name).FirstOrDefaultAsync(ct);
        return other is null ? (no, null) : (null, $"Admission no. {no} is already given to {other}.");
    }

    /// <summary>The admission fee head, if the centre charges one.</summary>
    public Task<FeeHead?> AdmissionFeeAsync(CancellationToken ct) =>
        db.FeeHeads.FirstOrDefaultAsync(h => h.IsAdmissionFee && !h.IsDeleted, ct);

    /// <summary>Waives the admission fee for one student: a 100% "Other" discount on the admission fee only (shows on their profile).</summary>
    public async Task WaiveAdmissionFeeAsync(Student s, CancellationToken ct)
    {
        if (await AdmissionFeeAsync(ct) is not { } head) return;
        s.StudentConcessions.Add(new StudentConcession
        {
            TenantId = s.TenantId,
            Reason = "Other",
            Note = "Admission fee waived",
            FeeHeadId = head.Id,
            Kind = "Percent",
            Value = 100,
            CreatedAt = DateTime.UtcNow
        });
    }

    // ---------- numbering settings ----------

    public async Task<AdmissionNumberingDto> GetNumberingAsync(CancellationToken ct)
    {
        var style = await StyleAsync(ct);
        var key = KeyFor(style, Clock.Today, await SessionStartAsync(ct));
        var last = await db.AdmissionSequences.Where(q => q.SessionYear == key).Select(q => (int?)q.LastNumber).FirstOrDefaultAsync(ct)
                   ?? (style == AdmissionNoStyles.Serial ? await HighestPlainNumberAsync(ct) : 0);
        return new AdmissionNumberingDto(style, last + 1, Format(style, key, last + 1));
    }

    /// <summary>Changes the style and, optionally, the number the next student gets (e.g. 1251 to continue the register).</summary>
    public async Task<(AdmissionNumberingDto? Result, string? Error)> SetNumberingAsync(Guid tenantId, string style, int? nextNumber, CancellationToken ct)
    {
        if (style is not (AdmissionNoStyles.Session or AdmissionNoStyles.Serial)) return (null, "Choose how admission numbers look.");
        if (nextNumber is < 1 or > 9_999_999) return (null, "Enter a number between 1 and 9999999.");

        await db.Tenants.Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.AdmissionNoStyle, style), ct);

        if (nextNumber is int next)
        {
            var key = KeyFor(style, Clock.Today, await SessionStartAsync(ct));
            var row = await db.AdmissionSequences.FirstOrDefaultAsync(q => q.SessionYear == key, ct);
            if (row is null) db.AdmissionSequences.Add(new AdmissionSequence { TenantId = tenantId, SessionYear = key, LastNumber = next - 1 });
            else row.LastNumber = next - 1;
            await db.SaveChangesAsync(ct);
        }
        return (await GetNumberingAsync(ct), null);
    }

    // ---------- confirmation ----------

    /// <summary>
    /// WhatsApp admission confirmation for a saved new admission (after this month's bills are raised).
    /// force = send even when the centre turned admission messages off (the "send again" button).
    /// False when skipped, the student is not a new admission, or the monthly message limit is used up.
    /// </summary>
    public async Task<bool> SendConfirmationAsync(Guid studentId, bool force, CancellationToken ct)
    {
        var s = await db.Students.AsNoTracking()
            .Where(x => x.Id == studentId)
            .Select(x => new { x.Id, x.Name, x.ParentName, x.ParentPhone, x.AdmissionNo, x.IsNewAdmission, Batch = x.Batch.Name })
            .FirstOrDefaultAsync(ct);
        if (s is null || !s.IsNewAdmission || string.IsNullOrEmpty(s.AdmissionNo)) return false;

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

    // ---------- helpers ----------

    /// <summary>Highest all-digit admission number in use (typed from the register or imported), 0 if none.</summary>
    private async Task<int> HighestPlainNumberAsync(CancellationToken ct)
    {
        var numbers = await db.Students.Where(s => s.AdmissionNo != null).Select(s => s.AdmissionNo!).ToListAsync(ct);
        return numbers.Select(n => n.All(char.IsDigit) && int.TryParse(n, out var v) ? v : 0).DefaultIfEmpty(0).Max();
    }

    /// <summary>First automatic plain number continues after the highest one already in use (1251 after 1250).</summary>
    private async Task EnsureSerialStartAsync(Guid tenantId, CancellationToken ct)
    {
        if (await db.AdmissionSequences.AnyAsync(q => q.SessionYear == 0, ct)) return;
        var start = await HighestPlainNumberAsync(ct);
        // Raw SQL so it never saves other pending changes; a row made at the same moment by another request wins.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            IF NOT EXISTS (SELECT 1 FROM AdmissionSequences WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND SessionYear = 0)
                INSERT INTO AdmissionSequences (TenantId, SessionYear, LastNumber) VALUES ({tenantId}, 0, {start});
            """, ct);
    }

    private async Task<string> StyleAsync(CancellationToken ct) =>
        await db.Tenants.Select(t => t.AdmissionNoStyle).FirstOrDefaultAsync(ct) ?? AdmissionNoStyles.Session;

    private async Task<int> SessionStartAsync(CancellationToken ct) =>
        await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;

    /// <summary>Sequence key: the session year ("Session" style) or 0 (one running series, "Serial" style).</summary>
    private static short KeyFor(string style, DateOnly joinedOn, int sessionStart) =>
        style == AdmissionNoStyles.Serial ? (short)0 : (short)FeeViews.SessionYear(joinedOn.ToString("yyyy-MM"), sessionStart);

    private static string Format(string style, short key, int n) =>
        style == AdmissionNoStyles.Serial ? n.ToString() : $"A-{key}-{n:D4}";

    /// <summary>Reserves "count" numbers for the key and returns the last one.</summary>
    private async Task<int> ReserveAsync(Guid tenantId, short key, int count, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>($"""
            DECLARE @n int;
            UPDATE AdmissionSequences WITH (UPDLOCK, HOLDLOCK)
               SET @n = LastNumber = LastNumber + {count}
             WHERE TenantId = {tenantId} AND SessionYear = {key};
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO AdmissionSequences (TenantId, SessionYear, LastNumber) VALUES ({tenantId}, {key}, {count});
                SET @n = {count};
            END
            SELECT @n AS [Value];
            """).ToListAsync(ct)).Single();
}
