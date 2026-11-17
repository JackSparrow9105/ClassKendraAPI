using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

// ---- Shapes that match models.ts (FeeDueView, PaymentView) ----
public record FeeDueView(
    string Id, string StudentId, string BatchId, string FeeHeadId, string HeadName,
    string Period, string PeriodLabel, string Month,
    int GrossAmount, int Discount, int Amount, int PaidAmount,
    string DueDate, string Status, string? LastPaidOn, int RemindersSent,
    string StudentName, string BatchName, int DaysLate, int Balance,
    string? Kind = null);

public record AllocationView(string DueId, string HeadName, string PeriodLabel, int Amount);

/// <param name="BalanceAfter">What the student owed right after this payment (from the frozen receipt).</param>
/// <param name="Receipt">Everything printed on the receipt, frozen at payment time. The receipt page draws only from this.</param>
/// <param name="Cancelled">true = this payment was cancelled: it counts for nothing and has no allocations left.</param>
/// <param name="Advance">Part of this payment not on any fee (kept as advance, or used on fees raised later).</param>
/// <param name="Together">Other receipts paid at the same time (brothers and sisters paid together).</param>
public record PaymentView(
    string Id, string ReceiptNo, string StudentId, int Amount, string Mode, string PaidOn,
    string Note, string ReceivedBy, List<AllocationView> Allocations,
    string StudentName, string BatchName, string ParentPhone, int BalanceAfter,
    ReceiptSnapshot Receipt,
    bool Cancelled = false, string? CancelledAt = null, string? CancelledBy = null, string? CancelReason = null,
    List<LinkedReceipt>? Together = null);

/// <summary>A receipt made in the same go as this one (another child of the family).</summary>
public record LinkedReceipt(string Id, string ReceiptNo, string StudentName, int Amount);

public static class FeeViews
{
    public static FeeDueView ToView(FeeDue d, string studentName, string batchName, DateOnly today)
    {
        var amount = d.Amount ?? d.GrossAmount - d.Discount;   // computed column comes back as int?
        return new(
            d.Id.ToString(), d.StudentId.ToString(), d.BatchId.ToString(), d.FeeHeadId.ToString(), d.HeadName,
            d.Period, d.PeriodLabel, d.Month,
            d.GrossAmount, d.Discount, amount, d.PaidAmount,
            d.DueDate.ToString("yyyy-MM-dd"), d.Status, d.LastPaidOn?.ToString("yyyy-MM-dd"), d.RemindersSent,
            studentName, batchName,
            d.Status != "Paid" ? today.DayNumber - d.DueDate.DayNumber : 0,   // negative = not due yet
            amount - d.PaidAmount,
            d.Kind);
    }

    /// <summary>Academic year a month belongs to. April session: "2027-02" → 2026.</summary>
    public static int SessionYear(string monthKey, int sessionStart)
    {
        var y = int.Parse(monthKey[..4]);
        var m = int.Parse(monthKey[5..7]);
        return m >= sessionStart ? y : y - 1;
    }
}

/// <summary>Builds the read-only views that several screens share.</summary>
public class FeeReadService(ClassKendraContext db, IConfiguration config)
{
    public string PayLink(string payToken) =>
        $"{(config["App:PublicUrl"] ?? "").TrimEnd('/')}/pay/{payToken}";

    /// <summary>Payments must be loaded with .Include(PaymentAllocations).ThenInclude(FeeDue).</summary>
    public async Task<List<PaymentView>> PaymentViews(List<Payment> payments, CancellationToken ct)
    {
        if (payments.Count == 0) return [];
        var ids = payments.Select(p => p.StudentId).Distinct().ToList();

        var students = await db.Students.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.ParentName, s.ParentPhone, BatchName = s.Batch.Name })
            .ToDictionaryAsync(s => s.Id, ct);

        // Receipts paid together with these (same GroupId), for "Paid together with Diya's receipt R-…".
        var groupIds = payments.Where(p => p.GroupId != null).Select(p => p.GroupId).Distinct().ToList();
        var together = (groupIds.Count == 0
                ? []
                : await db.Payments.AsNoTracking()
                    .Where(p => groupIds.Contains(p.GroupId))
                    .Select(p => new LinkedRow(p.Id, p.GroupId, p.ReceiptNo, p.Student.Name, p.Amount))
                    .ToListAsync(ct))
            .ToLookup(p => p.GroupId);

        var snapshots = payments.ToDictionary(p => p.Id, p => ReceiptSnapshots.Read(p.ReceiptSnapshot));
        if (snapshots.ContainsValue(null))
            await RebuildMissing(payments.Where(p => snapshots[p.Id] is null).ToList(), ids, snapshots,
                id => students.GetValueOrDefault(id) is { } s
                    ? new ReceiptStudent(s.Name, s.ParentName ?? "", s.ParentPhone ?? "", s.BatchName)
                    : new ReceiptStudent("Unknown", "", "", ""),
                ct);

        return payments.Select(p =>
        {
            var st = students.GetValueOrDefault(p.StudentId);
            var receipt = snapshots[p.Id]!;
            return new PaymentView(
                p.Id.ToString(), p.ReceiptNo, p.StudentId.ToString(), p.Amount, p.Mode,
                p.PaidOn.ToString("yyyy-MM-dd"), p.Note, p.ReceivedByName,
                p.PaymentAllocations
                    .Select(a => new AllocationView(a.FeeDueId.ToString(), a.FeeDue.HeadName, a.FeeDue.PeriodLabel, a.Amount))
                    .ToList(),
                st?.Name ?? "Unknown", st?.BatchName ?? "", st?.ParentPhone ?? "",
                receipt.BalanceAfter,
                receipt,
                p.CancelledAt is not null,
                p.CancelledAt is DateTime c ? DateTime.SpecifyKind(c, DateTimeKind.Utc).ToString("O") : null,
                p.CancelledByName, p.CancelReason,
                p.GroupId is null ? null : together[p.GroupId].Where(x => x.Id != p.Id)
                    .Select(x => new LinkedReceipt(x.Id.ToString(), x.ReceiptNo, x.Student ?? "", x.Amount)).ToList());
        }).ToList();
    }

    private record LinkedRow(Guid Id, Guid? GroupId, string ReceiptNo, string? Student, int Amount);

    /// <summary>
    /// Payments made before receipts were frozen: build the receipt once from today's data, mark it
    /// "rebuilt" and save it, so from now on it never changes either.
    /// </summary>
    private async Task RebuildMissing(
        List<Payment> missing, List<Guid> studentIds, Dictionary<Guid, ReceiptSnapshot?> snapshots,
        Func<Guid, ReceiptStudent> studentOf, CancellationToken ct)
    {
        var centre = await db.Tenants.AsNoTracking().FirstAsync(ct);

        // Balance after a payment = owed now + everything paid after it.
        var owedNow = await db.FeeDues
            .Where(d => studentIds.Contains(d.StudentId) && d.Status != "Cancelled")
            .GroupBy(d => d.StudentId)
            .Select(g => new { g.Key, Owed = g.Sum(d => (d.Amount ?? 0) - d.PaidAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Owed, ct);

        var allPayments = await db.Payments.AsNoTracking()
            .Where(p => studentIds.Contains(p.StudentId))
            .Select(p => new { p.StudentId, p.PaidOn, p.ReceiptNo, p.Amount })
            .ToListAsync(ct);

        foreach (var p in missing)
        {
            var paidLater = allPayments
                .Where(x => x.StudentId == p.StudentId &&
                            (x.PaidOn > p.PaidOn || (x.PaidOn == p.PaidOn && string.CompareOrdinal(x.ReceiptNo, p.ReceiptNo) > 0)))
                .Sum(x => x.Amount);

            var snapshot = ReceiptSnapshots.Build(
                p, centre, studentOf(p.StudentId),
                p.PaymentAllocations.Select(a => new ReceiptLine(a.FeeDue.HeadName, a.FeeDue.PeriodLabel, a.Amount)),
                owedNow.GetValueOrDefault(p.StudentId) + paidLater,
                rebuilt: true);
            snapshots[p.Id] = snapshot;

            // Only fills an empty column, so it never overwrites a receipt frozen at payment time.
            var json = ReceiptSnapshots.Serialize(snapshot);
            await db.Payments
                .Where(x => x.Id == p.Id && x.ReceiptSnapshot == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceiptSnapshot, json), ct);
        }
    }
}
/// <summary>One row per student on the Fees screen and the dashboard (StudentFees in models.ts).</summary>
public record StudentFees(
    string StudentId, string StudentName, string BatchId, string BatchName,
    List<FeeDueView> Dues, int Pending, int Paid, int MaxDaysLate, string? NextDueDate);

public static class StudentFeesRows
{
    /// <summary>Same grouping as groupByStudent() in fee.service.ts. Bills must include Student and Batch.</summary>
    public static List<StudentFees> Build(IEnumerable<FeeDue> bills, DateOnly today) =>
        bills.GroupBy(d => d.StudentId)
            .Select(g =>
            {
                var views = g.Select(d => FeeViews.ToView(d, d.Student.Name, d.Batch.Name, today))
                    .OrderBy(v => v.DueDate, StringComparer.Ordinal).ThenBy(v => v.HeadName)
                    .ToList();
                var unpaid = views.Where(v => v.Status != "Paid").ToList();
                var first = g.First();
                return new StudentFees(
                    first.StudentId.ToString(), first.Student.Name, first.BatchId.ToString(), first.Batch.Name,
                    views,
                    unpaid.Sum(v => v.Balance),
                    views.Sum(v => v.PaidAmount),
                    unpaid.Count == 0 ? 0 : Math.Max(0, unpaid.Max(v => v.DaysLate)),
                    unpaid.Count == 0 ? null : unpaid.Min(v => v.DueDate));
            })
            .ToList();
}
