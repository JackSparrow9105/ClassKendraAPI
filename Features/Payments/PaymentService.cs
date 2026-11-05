using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Payments;

public static class PaymentModes
{
    public const string Online = "Online";
    /// <summary>Modes staff can choose at the counter (PaymentMode in models.ts).</summary>
    public static readonly string[] Manual = ["Cash", "UPI", "Cheque"];
}

public record PaymentRequest(
    Guid StudentId, int Amount, string Mode, DateOnly PaidOn, string Note,
    IReadOnlyList<Guid>? DueIds, Guid? ReceivedByUserId, string ReceivedByName,
    string? RazorpayPaymentId = null, bool AllowExtra = false);

public class PaymentException(string message) : Exception(message);

/// <summary>Records a payment, spreads it over the student's bills and gives it a receipt number.</summary>
public class PaymentService(ClassKendraContext db, CurrentUser current)
{
    /// <summary>
    /// Money goes to the chosen bills first (in the order given), then to the oldest unpaid bills.
    /// Staff payments can't be more than what is owed. Online payments (AllowExtra) can be — e.g. the
    /// parent paid online while the same fee was also paid in cash; the extra stays on the receipt
    /// as unallocated and the student's balance shows as negative (in credit).
    /// Call inside a transaction.
    /// </summary>
    public async Task<Payment> RecordAsync(PaymentRequest r, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) throw new PaymentException("Set up your centre first.");
        if (r.Amount <= 0) throw new PaymentException("Enter an amount above zero.");

        // Lock this student's bills so two counters can't spend the same balance.
        var unpaid = await db.FeeDues
            .FromSqlInterpolated($"SELECT * FROM FeeDues WITH (UPDLOCK, ROWLOCK) WHERE StudentId = {r.StudentId}")
            .Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial)
            .ToListAsync(ct);

        var chosen = r.DueIds ?? [];
        // Online: the parent chose these bills earlier; any paid meanwhile are simply skipped.
        if (r.AllowExtra) chosen = chosen.Where(id => unpaid.Any(d => d.Id == id)).ToList();
        if (chosen.Any(id => unpaid.All(d => d.Id != id)))
            throw new PaymentException("One of the ticked fees is already paid or was removed. Reload and try again.");

        // Counter: only the ticked fees, oldest first. Online: the ticked fees, then any other unpaid fee.
        var ticked = unpaid.Where(d => chosen.Contains(d.Id)).OrderBy(d => d.DueDate).ThenBy(d => d.HeadName);
        var order = chosen.Count > 0 && !r.AllowExtra
            ? ticked.ToList()
            : ticked.Concat(unpaid.Where(d => !chosen.Contains(d.Id)).OrderBy(d => d.DueDate).ThenBy(d => d.HeadName)).ToList();

        var owed = order.Sum(Balance);
        if (r.Amount > owed && !r.AllowExtra)
            throw new PaymentException(owed == 0
                ? "Nothing is due for this student."
                : $"That's more than the {Money.Inr(owed)} due for the ticked fees.");

        var payment = new Payment
        {
            TenantId = tenantId,
            StudentId = r.StudentId,
            Amount = r.Amount,
            Mode = r.Mode,
            PaidOn = r.PaidOn,
            Note = r.Note,
            ReceivedByUserId = r.ReceivedByUserId,
            ReceivedByName = r.ReceivedByName,
            RazorpayPaymentId = r.RazorpayPaymentId,
            ReceiptNo = await NextReceiptNo(tenantId, r.PaidOn, ct),
            CreatedAt = DateTime.UtcNow
        };

        var left = r.Amount;
        foreach (var due in order)
        {
            if (left == 0) break;
            var take = Math.Min(left, Balance(due));
            if (take <= 0) continue;

            due.PaidAmount += take;
            due.Status = DueStatus.For(Amount(due), due.PaidAmount);
            if (due.LastPaidOn is null || due.LastPaidOn < r.PaidOn) due.LastPaidOn = r.PaidOn;
            payment.PaymentAllocations.Add(new PaymentAllocation { FeeDue = due, FeeDueId = due.Id, Amount = take });
            left -= take;
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>Undo a counter payment (wrong amount, wrong student). The receipt number is not reused.</summary>
    public async Task DeleteAsync(Payment p, CancellationToken ct)
    {
        foreach (var a in p.PaymentAllocations.ToList())
        {
            var due = a.FeeDue;
            due.PaidAmount = Math.Max(0, due.PaidAmount - a.Amount);
            if (due.Status != DueStatus.Cancelled) due.Status = DueStatus.For(Amount(due), due.PaidAmount);
            due.LastPaidOn = await db.PaymentAllocations
                .Where(x => x.FeeDueId == due.Id && x.PaymentId != p.Id)
                .MaxAsync(x => (DateOnly?)x.Payment.PaidOn, ct);
            db.PaymentAllocations.Remove(a);
        }
        db.Payments.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"R-2026-0001" — numbering restarts each session. Safe when two receipts are made at once.</summary>
    private async Task<string> NextReceiptNo(Guid tenantId, DateOnly paidOn, CancellationToken ct)
    {
        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var year = (short)FeeViews.SessionYear(paidOn.ToString("yyyy-MM"), sessionStart);

        var next = (await db.Database.SqlQuery<int>($"""
            DECLARE @n int;
            UPDATE ReceiptSequences WITH (UPDLOCK, HOLDLOCK)
               SET @n = LastNumber = LastNumber + 1
             WHERE TenantId = {tenantId} AND SessionYear = {year};
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO ReceiptSequences (TenantId, SessionYear, LastNumber) VALUES ({tenantId}, {year}, 1);
                SET @n = 1;
            END
            SELECT @n AS [Value];
            """).ToListAsync(ct)).Single();

        return $"R-{year}-{next:D4}";
    }

    private static int Amount(FeeDue d) => d.Amount ?? d.GrossAmount - d.Discount;
    private static int Balance(FeeDue d) => Amount(d) - d.PaidAmount;
}
