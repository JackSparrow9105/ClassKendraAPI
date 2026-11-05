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
    string StudentName, string BatchName, int DaysLate, int Balance);

public record AllocationView(string DueId, string HeadName, string PeriodLabel, int Amount);

public record PaymentView(
    string Id, string ReceiptNo, string StudentId, int Amount, string Mode, string PaidOn,
    string Note, string ReceivedBy, List<AllocationView> Allocations,
    string StudentName, string BatchName, string ParentPhone, int BalanceAfter);

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
            amount - d.PaidAmount);
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
            .Select(s => new { s.Id, s.Name, s.ParentPhone, BatchName = s.Batch.Name })
            .ToDictionaryAsync(s => s.Id, ct);

        // What each student owes right now.
        var owedNow = await db.FeeDues
            .Where(d => ids.Contains(d.StudentId) && d.Status != "Cancelled")
            .GroupBy(d => d.StudentId)
           .Select(g => new { g.Key, Owed = g.Sum(d => (d.Amount ?? 0) - d.PaidAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Owed, ct);

        var allPayments = await db.Payments.AsNoTracking()
            .Where(p => ids.Contains(p.StudentId))
            .Select(p => new { p.StudentId, p.PaidOn, p.ReceiptNo, p.Amount })
            .ToListAsync(ct);

        return payments.Select(p =>
        {
            var st = students.GetValueOrDefault(p.StudentId);
            // Balance after this payment = owed now + everything paid after it.
            var paidLater = allPayments
                .Where(x => x.StudentId == p.StudentId &&
                            (x.PaidOn > p.PaidOn || (x.PaidOn == p.PaidOn && string.CompareOrdinal(x.ReceiptNo, p.ReceiptNo) > 0)))
                .Sum(x => x.Amount);

            return new PaymentView(
                p.Id.ToString(), p.ReceiptNo, p.StudentId.ToString(), p.Amount, p.Mode,
                p.PaidOn.ToString("yyyy-MM-dd"), p.Note, p.ReceivedByName,
                p.PaymentAllocations
                    .Select(a => new AllocationView(a.FeeDueId.ToString(), a.FeeDue.HeadName, a.FeeDue.PeriodLabel, a.Amount))
                    .ToList(),
                st?.Name ?? "Unknown", st?.BatchName ?? "", st?.ParentPhone ?? "",
                owedNow.GetValueOrDefault(p.StudentId) + paidLater);
        }).ToList();
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
