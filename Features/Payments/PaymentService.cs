using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Payments;

public static class PaymentModes
{
    public const string Online = "Online";
    /// <summary>"Advance already paid" before the centre started using ClassKendra (opening balance). Never counted as collection.</summary>
    public const string Opening = "Opening";
    /// <summary>Modes staff can choose at the counter (PaymentMode in models.ts).</summary>
    public static readonly string[] Manual = ["Cash", "UPI", "Cheque"];
}

public record PaymentRequest(
    Guid StudentId, int Amount, string Mode, DateOnly PaidOn, string Note,
    IReadOnlyList<Guid>? DueIds, Guid? ReceivedByUserId, string ReceivedByName,
    string? RazorpayPaymentId = null, bool AllowExtra = false, bool KeepExtra = false, Guid? GroupId = null);

public class PaymentException(string message) : Exception(message);

/// <summary>Money a cancelled payment had put against one bill (kept in the activity log).</summary>
public record CancelledAllocation(Guid DueId, string HeadName, string PeriodLabel, int Amount);

/// <summary>Records a payment, spreads it over the student's bills and gives it a receipt number.</summary>
public class PaymentService(ClassKendraContext db, CurrentUser current, AdvanceService advances)
{
    /// <summary>
    /// Money goes to the chosen bills first (oldest first), then — for online payments (AllowExtra) and counter
    /// payments with KeepExtra — to the student's other unpaid bills, oldest first. Whatever is left after that
    /// stays on the payment as ADVANCE (shown on the receipt) and is used automatically on the next bills.
    /// Without KeepExtra a counter payment can't be more than the ticked bills (catches typing mistakes).
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

        // Counter: only the ticked fees, oldest first. Online and KeepExtra: the ticked fees, then any other unpaid fee.
        var extraGoesOn = r.AllowExtra || r.KeepExtra;
        var ticked = unpaid.Where(d => chosen.Contains(d.Id)).OrderBy(d => d.DueDate).ThenBy(d => d.HeadName);
        var order = chosen.Count > 0 && !extraGoesOn
            ? ticked.ToList()
            : ticked.Concat(unpaid.Where(d => !chosen.Contains(d.Id)).OrderBy(d => d.DueDate).ThenBy(d => d.HeadName)).ToList();

        var owed = order.Sum(Balance);
        if (r.Amount > owed && !extraGoesOn)
            throw new PaymentException(owed == 0
                ? "Nothing is due for this student. To take money in advance, tick \"Keep the extra as advance\"."
                : $"That's more than the {Money.Inr(owed)} due for the ticked fees. To keep the extra as advance, tick \"Keep the extra as advance\".");

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
            GroupId = r.GroupId,
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

        // Freeze what the receipt shows, as it is right now, in the same save as the payment.
        var centre = await db.Tenants.AsNoTracking().FirstAsync(ct);
        var student = await db.Students.AsNoTracking()
            .Where(s => s.Id == r.StudentId)
            .Select(s => new ReceiptStudent(s.Name, s.ParentName ?? "", s.ParentPhone ?? "", s.Batch.Name))
            .FirstAsync(ct);
        var lines = payment.PaymentAllocations
            .Select(a => new ReceiptLine(a.FeeDue.HeadName, a.FeeDue.PeriodLabel, a.Amount));
        var balanceAfter = unpaid.Sum(Balance) - left;   // what the student still owes after this payment
        payment.ReceiptSnapshot = ReceiptSnapshots.Serialize(
            ReceiptSnapshots.Build(payment, centre, student, lines, balanceAfter, rebuilt: false));

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>
    /// Cancel a counter payment (wrong amount, wrong student, cheque bounced). Nothing is deleted:
    /// the money comes back off the bills, the payment row stays with who cancelled it and why,
    /// and its receipt number is never reused. Returns what was taken back, for the activity log.
    /// The payment must be loaded with .Include(PaymentAllocations).ThenInclude(FeeDue). Call inside a transaction.
    /// </summary>
    public async Task<List<CancelledAllocation>> CancelAsync(Payment p, string reason, string byName, CancellationToken ct)
    {
        // Advance from this payment that was refunded can't be taken back by cancelling the payment.
        var unused = p.Amount - p.PaymentAllocations.Sum(a => a.Amount);
        if (unused > 0)
        {
            var advance = await advances.ForAsync(p.StudentId, ct);
            if (advance < unused)
                throw new PaymentException(
                    $"{Money.Inr(unused - advance)} of this payment's advance was already refunded, so it can't be cancelled.");
        }

        var undone = new List<CancelledAllocation>();
        foreach (var a in p.PaymentAllocations.ToList())
        {
            var due = a.FeeDue;
            due.PaidAmount = Math.Max(0, due.PaidAmount - a.Amount);
            if (due.Status != DueStatus.Cancelled) due.Status = DueStatus.For(Amount(due), due.PaidAmount);
            due.LastPaidOn = await db.PaymentAllocations
                .Where(x => x.FeeDueId == due.Id && x.PaymentId != p.Id)
                .MaxAsync(x => (DateOnly?)x.Payment.PaidOn, ct);
            undone.Add(new CancelledAllocation(due.Id, due.HeadName, due.PeriodLabel, a.Amount));
            db.PaymentAllocations.Remove(a);
        }

        p.CancelledAt = DateTime.UtcNow;
        p.CancelledByUserId = current.IsSystem ? null : current.UserId;
        p.CancelledByName = byName;
        p.CancelReason = reason;
        await db.SaveChangesAsync(ct);
        return undone;
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

    /// <summary>Gross − discount (FeeDues.Amount is a computed column and can be stale in memory).</summary>
    private static int Amount(FeeDue d) => d.GrossAmount - d.Discount;
    private static int Balance(FeeDue d) => Amount(d) - d.PaidAmount;
}
