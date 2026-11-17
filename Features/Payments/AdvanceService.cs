using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Payments;

/// <summary>
/// A student's advance = money paid that isn't on any bill yet:
///     sum(payment amounts) − sum(what those payments put on bills) − sum(refunds)
/// Cancelled payments don't count (the query filter skips them). There is no separate balance column,
/// so the advance can never drift from the receipts.
///
/// Whenever a bill is raised or goes up, ApplyAsync uses the advance on the student's unpaid bills, oldest first,
/// taking the money from the oldest payment that still has some left.
/// </summary>
public class AdvanceService(ClassKendraContext db, ILogger<AdvanceService> log)
{
    /// <summary>Advance per student (only students with more than ₹0 are in the result).</summary>
    public async Task<Dictionary<Guid, int>> ForAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken ct)
    {
        if (studentIds.Count == 0) return [];
        var paid = await db.Payments.AsNoTracking()
            .Where(p => studentIds.Contains(p.StudentId))
            .GroupBy(p => p.StudentId)
            .Select(g => new { g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        if (paid.Count == 0) return [];

        var used = await db.PaymentAllocations.AsNoTracking()
            .Where(a => studentIds.Contains(a.Payment.StudentId) && a.Payment.CancelledAt == null)
            .GroupBy(a => a.Payment.StudentId)
            .Select(g => new { g.Key, Total = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var refunded = await db.Refunds.AsNoTracking()
            .Where(r => studentIds.Contains(r.StudentId))
            .GroupBy(r => r.StudentId)
            .Select(g => new { g.Key, Total = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        return paid
            .Select(p => (p.Key, Left: p.Value - used.GetValueOrDefault(p.Key) - refunded.GetValueOrDefault(p.Key)))
            .Where(x => x.Left > 0)
            .ToDictionary(x => x.Key, x => x.Left);
    }

    public async Task<int> ForAsync(Guid studentId, CancellationToken ct) =>
        (await ForAsync([studentId], ct)).GetValueOrDefault(studentId);

    /// <summary>
    /// Uses each student's advance on their unpaid bills, oldest due date first. Returns how much was used.
    /// Safe to call any time; does nothing for students with no advance. Runs in its own transaction
    /// unless one is already open, and locks the students' bills and payments while it works.
    /// </summary>
    public async Task<int> ApplyAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken ct)
    {
        if (studentIds.Count == 0) return 0;
        var withAdvance = (await ForAsync(studentIds, ct)).Keys.ToList();
        if (withAdvance.Count == 0) return 0;

        var ownTx = db.Database.CurrentTransaction is null;
        await using var tx = ownTx ? await db.Database.BeginTransactionAsync(ct) : null;
        var usedTotal = 0;
        try
        {
            foreach (var studentId in withAdvance)
                usedTotal += await ApplyOneAsync(studentId, ct);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ownTx && ex is DbUpdateException)
        {
            // Another request changed the same bills at the same moment. The next call will use the advance.
            log.LogWarning(ex, "Advance not applied this time");
            db.ChangeTracker.Clear();
            return 0;
        }
        return usedTotal;
    }

    private async Task<int> ApplyOneAsync(Guid studentId, CancellationToken ct)
    {
        // Lock this student's bills and payments, as PaymentService does, so two requests can't spend the same money.
        var bills = await db.FeeDues
            .FromSqlInterpolated($"SELECT * FROM FeeDues WITH (UPDLOCK, ROWLOCK) WHERE StudentId = {studentId}")
            .Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial)
            .OrderBy(d => d.DueDate).ThenBy(d => d.HeadName)
            .ToListAsync(ct);
        if (bills.Count == 0) return 0;

        var payments = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM Payments WITH (UPDLOCK, ROWLOCK) WHERE StudentId = {studentId}")
            .Include(p => p.PaymentAllocations)
            .OrderBy(p => p.PaidOn).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
        var refunded = await db.Refunds.Where(r => r.StudentId == studentId).SumAsync(r => (int?)r.Amount, ct) ?? 0;

        var plan = Plan(
            payments.Select(p => (p.Id, p.Amount - p.PaymentAllocations.Sum(a => a.Amount))).ToList(),
            refunded,
            bills.Select(b => (b.Id, Amount(b) - b.PaidAmount)).ToList());

        var byPayment = payments.ToDictionary(p => p.Id);
        var byBill = bills.ToDictionary(b => b.Id);
        foreach (var (paymentId, billId, take) in plan)
        {
            var p = byPayment[paymentId];
            var bill = byBill[billId];
            var existing = p.PaymentAllocations.FirstOrDefault(a => a.FeeDueId == billId);
            if (existing is not null) existing.Amount += take;
            else p.PaymentAllocations.Add(new PaymentAllocation { PaymentId = p.Id, FeeDueId = billId, Amount = take });

            bill.PaidAmount += take;
            if (bill.LastPaidOn is null || bill.LastPaidOn < p.PaidOn) bill.LastPaidOn = p.PaidOn;
            bill.Status = DueStatus.For(Amount(bill), bill.PaidAmount);
        }
        return plan.Sum(x => x.Amount);
    }

    /// <summary>
    /// The arithmetic of ApplyAsync, without the database (so it can be tested on its own).
    /// payments: (id, money not on any bill) oldest first. refunded: total refunds, taken from the oldest money first.
    /// bills: (id, still owed) in the order to pay them. Returns which payment pays how much of which bill.
    /// </summary>
    public static List<(Guid PaymentId, Guid BillId, int Amount)> Plan(
        IReadOnlyList<(Guid Id, int Left)> payments, int refunded, IReadOnlyList<(Guid Id, int Owed)> bills)
    {
        var free = new List<(Guid Id, int Left)>();
        foreach (var (id, l) in payments)
        {
            var left = l;
            if (left <= 0) continue;
            var takenByRefund = Math.Min(left, Math.Max(0, refunded));
            refunded -= takenByRefund;
            left -= takenByRefund;
            if (left > 0) free.Add((id, left));
        }

        var plan = new List<(Guid, Guid, int)>();
        var i = 0;
        foreach (var (billId, o) in bills)
        {
            var owed = o;
            while (owed > 0 && i < free.Count)
            {
                var (pid, left) = free[i];
                var take = Math.Min(owed, left);
                plan.Add((pid, billId, take));
                owed -= take;
                free[i] = (pid, left - take);
                if (left - take == 0) i++;
            }
            if (i >= free.Count) break;
        }
        return plan;
    }

    /// <summary>
    /// A bill went down (part waived) below what was already paid on it: take the extra back off its allocations,
    /// newest payment first. That money becomes advance again. Call inside a transaction, then SaveChanges.
    /// </summary>
    public async Task<int> ReleaseAsync(FeeDue bill, int amount, CancellationToken ct)
    {
        if (amount <= 0) return 0;
        var allocations = await db.PaymentAllocations
            .Include(a => a.Payment)
            .Where(a => a.FeeDueId == bill.Id)
            .OrderByDescending(a => a.Payment.PaidOn).ThenByDescending(a => a.Payment.CreatedAt)
            .ToListAsync(ct);

        var released = 0;
        foreach (var a in allocations)
        {
            if (released == amount) break;
            var take = Math.Min(a.Amount, amount - released);
            a.Amount -= take;
            released += take;
            if (a.Amount == 0) db.PaymentAllocations.Remove(a);
        }
        bill.PaidAmount -= released;
        bill.LastPaidOn = allocations.Where(a => a.Amount > 0).Select(a => (DateOnly?)a.Payment.PaidOn).Max();
        return released;
    }

    /// <summary>Gross − discount. (FeeDues.Amount is a computed column, stale until saved, so it isn't used here.)</summary>
    private static int Amount(FeeDue d) => d.GrossAmount - d.Discount;
}
