using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public static class LateFeeTypes
{
    public const string None = "None";
    /// <summary>A fixed fine once the bill is LateFeeAfterDays past its due date.</summary>
    public const string Once = "Once";
    /// <summary>₹ per day after LateFeeAfterDays, up to LateFeeMax (0 = no limit).</summary>
    public const string PerDay = "PerDay";
    public static readonly string[] All = [None, Once, PerDay];
}

/// <summary>
/// Late fees, raised by the daily job (and right after the owner saves fee settings).
/// Each overdue bill of the centre's own fees gets at most one late-fee bill (ParentDueId), labelled with
/// the fee and period it is for. A per-day late fee grows every day while the bill is unpaid and stops
/// the day the bill is paid; it never goes down by itself. A late fee the office waived completely
/// (cancelled) is never raised again for that bill. Students who left get no new late fees.
/// </summary>
public class LateFeeService(ClassKendraContext db, CurrentUser current, AdvanceService advances, ILogger<LateFeeService> log)
{
    public const string HeadName = "Late fee";

    /// <summary>The late fee a bill should carry today (0 = none yet).</summary>
    public static int Expected(FeeSetting s, DateOnly dueDate, DateOnly today)
    {
        var lateDays = today.DayNumber - dueDate.DayNumber - s.LateFeeAfterDays;
        if (lateDays <= 0 || s.LateFeeAmount <= 0) return 0;
        return s.LateFeeType switch
        {
            LateFeeTypes.Once => s.LateFeeAmount,
            LateFeeTypes.PerDay => s.LateFeeMax > 0
                ? (int)Math.Min(s.LateFeeMax, (long)s.LateFeeAmount * lateDays)
                : (int)Math.Min(1_000_000, (long)s.LateFeeAmount * lateDays),
            _ => 0
        };
    }

    /// <summary>Raises or updates late fees for this centre. Returns how many late-fee bills changed.</summary>
    public async Task<int> RaiseAsync(CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return 0;
        var s = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null || s.LateFeeType is not (LateFeeTypes.Once or LateFeeTypes.PerDay) || s.LateFeeAmount <= 0) return 0;

        var today = Clock.Today;
        var lastDue = today.AddDays(-s.LateFeeAfterDays - 1);

        // The centre's own fees, still unpaid, of students still studying here, overdue past the grace days.
        var overdue = await db.FeeDues.AsNoTracking()
            .Where(d => d.Kind == null && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial)
                        && d.DueDate <= lastDue && d.Student.IsActive)
            .Select(d => new { d.Id, d.StudentId, d.BatchId, d.HeadName, d.PeriodLabel, d.Month, d.DueDate })
            .ToListAsync(ct);
        if (overdue.Count == 0) return 0;

        var ids = overdue.Select(d => (Guid?)d.Id).ToList();
        var existing = await db.FeeDues
            .Where(d => d.Kind == BillKinds.LateFee && ids.Contains(d.ParentDueId))
            .ToDictionaryAsync(d => d.ParentDueId!.Value, ct);

        FeeHead? head = null;
        var changed = 0;
        var students = new HashSet<Guid>();
        foreach (var bill in overdue)
        {
            var fee = Expected(s, bill.DueDate, today);
            if (fee <= 0) continue;

            if (existing.TryGetValue(bill.Id, out var late))
            {
                if (late.Status == DueStatus.Cancelled) continue;   // waived by the office: don't charge again
                if (fee <= late.GrossAmount) continue;              // a late fee only ever goes up by itself
                late.GrossAmount = fee;
                late.Status = DueStatus.For(late.GrossAmount - late.Discount, late.PaidAmount);
            }
            else
            {
                head ??= await BillKinds.HeadAsync(db, tenantId, BillKinds.LateFee, ct);
                db.FeeDues.Add(new FeeDue
                {
                    TenantId = tenantId,
                    StudentId = bill.StudentId,
                    BatchId = bill.BatchId,
                    FeeHeadId = head.Id,
                    Kind = BillKinds.LateFee,
                    ParentDueId = bill.Id,
                    HeadName = HeadName,
                    Period = BillKinds.Period('L', bill.Id.ToString()),
                    PeriodLabel = BillKinds.Fit($"{bill.HeadName} · {bill.PeriodLabel}", 40),
                    Month = bill.Month,
                    GrossAmount = fee,
                    Discount = 0,
                    PaidAmount = 0,
                    DueDate = today,
                    Status = DueStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                });
            }
            students.Add(bill.StudentId);
            changed++;
        }

        if (changed == 0) return 0;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two runs at the same moment: the other one raised them. Nothing lost.
            log.LogWarning(ex, "Late fees clashed for tenant {Tenant}", tenantId);
            db.ChangeTracker.Clear();
            return 0;
        }
        await advances.ApplyAsync(students.ToList(), ct);
        return changed;
    }
}
