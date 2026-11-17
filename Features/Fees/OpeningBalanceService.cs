using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

/// <summary>What a student owed, or had paid ahead, before the centre started using ClassKendra (OpeningBalance in models.ts).</summary>
/// <param name="Editable">False once money has been paid against the previous dues or the advance has been used/refunded.</param>
public record OpeningBalanceView(int PreviousDues, int AdvancePaid, string? AsOn, bool Editable);

public class OpeningBalanceException(string message) : Exception(message);

/// <summary>
/// Opening balances for centres moving to ClassKendra mid-year:
///  - Previous dues → one bill "Previous dues" (period "OB", label "Before ClassKendra"), due on the date given.
///  - Advance already paid → one entry of mode "Opening" (number "OB-…"), which never counts as this month's
///    collection; it is used on the student's bills like any advance.
/// Both can be changed until money is paid against the previous dues or the advance is used.
/// </summary>
public class OpeningBalanceService(ClassKendraContext db, CurrentUser current, AdvanceService advances)
{
    public const string Label = "Before ClassKendra";
    public const string HeadName = "Previous dues";

    public async Task<OpeningBalanceView> GetAsync(Guid studentId, CancellationToken ct)
    {
        var bill = await Bill(studentId).AsNoTracking().FirstOrDefaultAsync(ct);
        var pay = await Opening(studentId).AsNoTracking().Include(p => p.PaymentAllocations).FirstOrDefaultAsync(ct);
        var dues = bill is { Status: not DueStatus.Cancelled } ? bill.GrossAmount - bill.Discount : 0;
        var asOn = bill?.DueDate ?? pay?.PaidOn;
        var editable = (bill is null || bill.PaidAmount == 0)
                       && (pay is null || (pay.PaymentAllocations.Count == 0 && await NotRefundedAsync(studentId, pay.Amount, ct)));
        return new OpeningBalanceView(dues, pay?.Amount ?? 0, asOn?.ToString("yyyy-MM-dd"), editable);
    }

    /// <summary>Sets both amounts (0 = none). Call inside a transaction; uses the advance on the student's bills afterwards.</summary>
    public async Task SetAsync(Guid studentId, int previousDues, int advancePaid, DateOnly asOn, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) throw new OpeningBalanceException("Set up your centre first.");
        if (previousDues < 0 || advancePaid < 0) throw new OpeningBalanceException("Amounts can't be negative.");
        if (previousDues > 10_000_000 || advancePaid > 10_000_000) throw new OpeningBalanceException("That amount looks too large.");
        if (asOn > Clock.Today) throw new OpeningBalanceException("The date can't be in the future.");

        var student = await db.Students.FirstOrDefaultAsync(s => s.Id == studentId, ct)
                      ?? throw new OpeningBalanceException("Student not found.");

        // ----- previous dues -----
        var bill = await Bill(studentId).FirstOrDefaultAsync(ct);
        var currentDues = bill is { Status: not DueStatus.Cancelled } ? bill.GrossAmount - bill.Discount : 0;
        if (bill is not null && (currentDues != previousDues || bill.DueDate != asOn))
        {
            if (bill.PaidAmount > 0)
                throw new OpeningBalanceException(
                    $"{Money.Inr(bill.PaidAmount)} is already paid against the previous dues, so they can't be changed. Waive part of the bill instead.");
            if (previousDues == 0)
            {
                bill.Status = DueStatus.Cancelled;
                bill.CancelledAt = DateTime.UtcNow;
            }
            else
            {
                bill.GrossAmount = previousDues;
                bill.Discount = 0;
                bill.DueDate = asOn;
                bill.Status = DueStatus.Pending;
                bill.CancelledAt = null;
            }
        }
        else if (bill is null && previousDues > 0)
        {
            var head = await BillKinds.HeadAsync(db, tenantId, BillKinds.OpeningBalance, ct);
            db.FeeDues.Add(new FeeDue
            {
                TenantId = tenantId,
                StudentId = studentId,
                BatchId = student.BatchId,
                FeeHeadId = head.Id,
                Kind = BillKinds.OpeningBalance,
                HeadName = HeadName,
                Period = BillKinds.OpeningPeriod,
                PeriodLabel = Label,
                Month = asOn.ToString("yyyy-MM"),
                GrossAmount = previousDues,
                Discount = 0,
                PaidAmount = 0,
                DueDate = asOn,
                Status = DueStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
        }

        // ----- advance already paid -----
        var pay = await Opening(studentId).Include(p => p.PaymentAllocations).FirstOrDefaultAsync(ct);
        if (pay is not null && (pay.Amount != advancePaid || pay.PaidOn != asOn))
        {
            if (pay.PaymentAllocations.Count > 0 || !await NotRefundedAsync(studentId, pay.Amount, ct))
                throw new OpeningBalanceException("The advance paid before ClassKendra has already been used on fees, so it can't be changed.");
            if (advancePaid == 0) db.Payments.Remove(pay);   // an opening entry, not a receipt: nothing to keep
            else
            {
                pay.Amount = advancePaid;
                pay.PaidOn = asOn;
                pay.ReceiptSnapshot = null;   // rebuilt with the new amount when next shown
            }
        }
        else if (pay is null && advancePaid > 0)
        {
            db.Payments.Add(new Payment
            {
                TenantId = tenantId,
                StudentId = studentId,
                Amount = advancePaid,
                Mode = PaymentModes.Opening,
                PaidOn = asOn,
                Note = "Advance paid before ClassKendra",
                ReceivedByName = "Opening balance",
                ReceivedByUserId = null,
                // One opening entry per student: "OB-" + 16 letters from the student's id (fits ReceiptNo's 20).
                ReceiptNo = "OB-" + BillKinds.Period('X', studentId.ToString())[1..] + BillKinds.Period('X', "ob" + studentId)[1..8],
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        await advances.ApplyAsync([studentId], ct);
    }

    private IQueryable<FeeDue> Bill(Guid studentId) =>
        db.FeeDues.Where(d => d.StudentId == studentId && d.Kind == BillKinds.OpeningBalance && d.Period == BillKinds.OpeningPeriod);

    private IQueryable<Payment> Opening(Guid studentId) =>
        db.Payments.Where(p => p.StudentId == studentId && p.Mode == PaymentModes.Opening);

    /// <summary>True while none of this money could have gone out as a refund (refunds come from the advance).</summary>
    private async Task<bool> NotRefundedAsync(Guid studentId, int amount, CancellationToken ct) =>
        await advances.ForAsync(studentId, ct) >= amount;
}
