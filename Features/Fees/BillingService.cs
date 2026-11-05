using System.Globalization;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public static class DueStatus
{
    public const string Pending = "Pending";
    public const string Partial = "Partial";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";

    public static string For(int amount, int paid) =>
        paid >= amount ? Paid : paid > 0 ? Partial : Pending;
}

/// <summary>A billing period for one fee head, e.g. "2026-11" / "Nov 2026" or "2026-Q3" / "Oct–Dec 2026".</summary>
public record BillPeriod(string Key, string Label);

/// <summary>
/// Raises bills (FeeDues) and keeps their discounts in line with concessions.
/// Same rules as the Angular app (core/fees/schedule.ts and core/fees/concessions.ts):
///
///  Monthly     every month
///  Quarterly   session start month, +3, +6, +9 — or the month a student joins
///  HalfYearly  session start month and +6 — or the month a student joins
///  Yearly      session start month — or the month a student joins
///  OneTime     only in the month the student joins (existing students are never charged)
///
/// - Bills are raised for the current month. Running it twice never creates duplicates:
///   (student, fee head, period) is unique in the database. Earlier months are not back-filled.
/// - Due date = the due day of the month the bill is raised in.
/// - Concessions for this head and for "all heads" both apply; percentages first, then fixed amounts;
///   never more than the bill. A bill with nothing left to pay (100% concession) is not raised.
/// - A bill cancelled earlier (student opted out, then back in) is revived when it applies again.
/// </summary>
public class BillingService(ClassKendraContext db, CurrentUser current, ILogger<BillingService> log)
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>Raise this month's bills. Pass student ids to limit it to those students.</summary>
    public async Task<int> RaiseAsync(IReadOnlyCollection<Guid>? studentIds, CancellationToken ct, DateOnly? forMonth = null)
    {
        try
        {
            return await RaiseOnceAsync(studentIds, forMonth, ct);
        }
        catch (DbUpdateException ex)
        {
            // Another request raised the same bill at the same moment. Start clean and try once more.
            log.LogWarning(ex, "Bill raising clashed for tenant {Tenant}; retrying", current.TenantId);
            db.ChangeTracker.Clear();
            return await RaiseOnceAsync(studentIds, forMonth, ct);
        }
    }

    private async Task<int> RaiseOnceAsync(IReadOnlyCollection<Guid>? studentIds, DateOnly? forMonth, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return 0;

        var today = Clock.Today;
        var month = forMonth ?? today;
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var monthKey = monthStart.ToString("yyyy-MM", En);

        var settings = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        int dueDay = settings?.DueDay ?? 10, sessionStart = settings?.SessionStartMonth ?? 4;

        var heads = await db.FeeHeads.AsNoTracking()
            .Include(h => h.FeeHeadAmounts)
            .Where(h => !h.IsDeleted)
            .ToListAsync(ct);
        if (heads.Count == 0) return 0;

        var studentsQ = db.Students.AsNoTracking().Where(s => s.IsActive && s.JoinedOn <= monthEnd);
        if (studentIds is { Count: > 0 }) studentsQ = studentsQ.Where(s => studentIds.Contains(s.Id));
        var students = await studentsQ.Select(s => new { s.Id, s.BatchId, s.JoinedOn }).ToListAsync(ct);
        if (students.Count == 0) return 0;
        var ids = students.Select(s => s.Id).ToList();

        // Who opted in to which optional fee.
        var optionalHeadIds = heads.Where(h => h.IsOptional).Select(h => h.Id).ToList();
        var optedIn = optionalHeadIds.Count == 0
            ? new HashSet<(Guid, Guid)>()
            : (await db.Set<Dictionary<string, object>>("StudentOptionalFeeHead")
                    .Where(x => optionalHeadIds.Contains(EF.Property<Guid>(x, "FeeHeadId")))
                    .Select(x => new { S = EF.Property<Guid>(x, "StudentId"), H = EF.Property<Guid>(x, "FeeHeadId") })
                    .ToListAsync(ct))
                .Select(x => (x.S, x.H)).ToHashSet();

        var concessions = (await db.StudentConcessions.AsNoTracking()
                .Where(c => ids.Contains(c.StudentId)).ToListAsync(ct))
            .ToLookup(c => c.StudentId);

        var periodByHead = heads.ToDictionary(h => h.Id, h => PeriodFor(h.Frequency, monthStart, sessionStart));
        var periodKeys = periodByHead.Values.Select(p => p.Key).Distinct().ToList();

        // Bills that already exist for these periods (cancelled ones included — they may need reviving).
        var existing = await db.FeeDues
            .Where(d => ids.Contains(d.StudentId) && periodKeys.Contains(d.Period))
            .ToListAsync(ct);
        var existingByKey = existing.ToDictionary(d => (d.StudentId, d.FeeHeadId, d.Period));

        var dueDate = new DateOnly(monthStart.Year, monthStart.Month, Math.Clamp(dueDay, 1, 28));
        var created = 0;

        foreach (var st in students)
        {
            foreach (var head in heads)
            {
                var gross = head.FeeHeadAmounts.FirstOrDefault(a => a.BatchId == st.BatchId)?.Amount ?? 0;
                if (gross <= 0) continue;
                if (head.IsOptional && !optedIn.Contains((st.Id, head.Id))) continue;
                if (!IsDueIn(head.Frequency, monthStart, sessionStart, st.JoinedOn)) continue;

                var period = periodByHead[head.Id];
                var discount = Discount(gross, concessions[st.Id], head.Id);
                if (gross - discount <= 0) continue;   // full scholarship: nothing to bill

                if (existingByKey.TryGetValue((st.Id, head.Id, period.Key), out var bill))
                {
                    if (bill.Status == DueStatus.Cancelled && bill.PaidAmount == 0)
                    {
                        bill.Status = DueStatus.For(gross - discount, 0);
                        bill.CancelledAt = null;
                        bill.GrossAmount = gross;
                        bill.Discount = discount;
                        bill.BatchId = st.BatchId;
                        bill.HeadName = head.Name;
                        created++;
                    }
                    continue;
                }

                db.FeeDues.Add(new FeeDue
                {
                    TenantId = tenantId,
                    StudentId = st.Id,
                    BatchId = st.BatchId,
                    FeeHeadId = head.Id,
                    HeadName = head.Name,
                    Period = period.Key,
                    PeriodLabel = period.Label,
                    Month = monthKey,
                    GrossAmount = gross,
                    Discount = discount,
                    PaidAmount = 0,
                    DueDate = dueDate,
                    Status = DueStatus.For(gross - discount, 0),
                    CreatedAt = DateTime.UtcNow
                });
                created++;
            }
        }

        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// After a student's concessions change, re-price their bills that have no money against them yet.
    /// A bill that drops to ₹0 is cancelled. Then this month's bills are raised again in case one now applies.
    /// </summary>
    public async Task RepriceAsync(Guid studentId, CancellationToken ct)
    {
        var concessions = await db.StudentConcessions.AsNoTracking()
            .Where(c => c.StudentId == studentId).ToListAsync(ct);

        var bills = await db.FeeDues
            .Where(d => d.StudentId == studentId && d.Status == DueStatus.Pending && d.PaidAmount == 0)
            .ToListAsync(ct);

        foreach (var b in bills)
        {
            b.Discount = Discount(b.GrossAmount, concessions, b.FeeHeadId);
            if (b.GrossAmount - b.Discount <= 0)
            {
                b.Status = DueStatus.Cancelled;
                b.CancelledAt = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await RaiseAsync([studentId], ct);
    }

    /// <summary>Percentages on the gross amount first, then fixed amounts; never more than the gross.</summary>
    public static int Discount(int gross, IEnumerable<StudentConcession> concessions, Guid headId)
    {
        var applicable = concessions.Where(c => c.FeeHeadId == null || c.FeeHeadId == headId).ToList();
        var total = 0;
        foreach (var c in applicable.Where(c => c.Kind == "Percent"))
            total += (int)Math.Round(gross * c.Value / 100m, MidpointRounding.AwayFromZero);
        foreach (var c in applicable.Where(c => c.Kind == "Amount"))
            total += c.Value;
        return Math.Clamp(total, 0, gross);
    }

    /// <summary>Does a fee with this frequency fall due in this month for a student who joined on joinedOn?</summary>
    public static bool IsDueIn(string frequency, DateOnly month, int sessionStartMonth, DateOnly joinedOn)
    {
        var joinedMonth = new DateOnly(joinedOn.Year, joinedOn.Month, 1);
        if (month < joinedMonth) return false;
        var off = (month.Month - sessionStartMonth + 12) % 12;
        var joinedThisMonth = month == joinedMonth;
        return frequency switch
        {
            "Monthly" => true,
            "Quarterly" => off % 3 == 0 || joinedThisMonth,
            "HalfYearly" => off % 6 == 0 || joinedThisMonth,
            "Yearly" => off == 0 || joinedThisMonth,
            "OneTime" => joinedThisMonth,
            _ => false
        };
    }

    /// <summary>Same keys and labels as periodFor() in core/fees/schedule.ts.</summary>
    public static BillPeriod PeriodFor(string frequency, DateOnly month, int sessionStartMonth)
    {
        var sy = month.Month >= sessionStartMonth ? month.Year : month.Year - 1;
        var off = (month.Month - sessionStartMonth + 12) % 12;
        var session = $"{sy}–{(sy + 1) % 100:D2}";

        switch (frequency)
        {
            case "Quarterly":
                var q = off / 3 + 1;
                var from = new DateOnly(2000, (sessionStartMonth - 1 + (q - 1) * 3) % 12 + 1, 1).ToString("MMM", En);
                var to = new DateOnly(2000, (sessionStartMonth - 1 + (q - 1) * 3 + 2) % 12 + 1, 1).ToString("MMM", En);
                return new BillPeriod($"{sy}-Q{q}", $"Q{q} · {from}–{to}");
            case "HalfYearly":
                var t = off < 6 ? 1 : 2;
                return new BillPeriod($"{sy}-T{t}", $"Term {t} · {session}");
            case "Yearly":
                return new BillPeriod($"{sy}-Y", session);
            case "OneTime":
                return new BillPeriod("OT", "One-time");
            default:
                return new BillPeriod(month.ToString("yyyy-MM", En), month.ToString("MMM yyyy", En));
        }
    }
}
