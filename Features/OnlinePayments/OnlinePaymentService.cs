using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using ClassKendraAPI.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.OnlinePayments;

public static class OrderStatus
{
    public const string Created = "Created";
    public const string Paid = "Paid";
    public const string Failed = "Failed";
}

/// <summary>
/// Finishes Razorpay orders. Called from the checkout success callback AND the webhook —
/// whichever comes first records the payment; the other finds it already done.
/// </summary>
public class OnlinePaymentService(
    ClassKendraContext db, CurrentUser current, PaymentService payments,
    FeeReadService views, MessageService messages, ILogger<OnlinePaymentService> log)
{
    /// <summary>Records a parent's fee payment. Returns the payment id, or null if the order isn't a fee order.</summary>
    public async Task<Guid?> CompleteFeeOrderAsync(string orderId, string razorpayPaymentId, CancellationToken ct)
    {
        Payment payment;
        bool isNew;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var order = await db.PaymentOrders
                .FromSqlInterpolated($"SELECT * FROM PaymentOrders WITH (UPDLOCK, ROWLOCK) WHERE RazorpayOrderId = {orderId}")
                .IgnoreQueryFilters()   // no signed-in user here; the tenant comes from the order itself
                .Include(o => o.FeeDues)
                .FirstOrDefaultAsync(ct);
            if (order is null) return null;

            UseTenant(order.TenantId);

            if (order.Status == OrderStatus.Paid && order.PaymentId is Guid done) return done;

            var existing = await db.Payments.FirstOrDefaultAsync(p => p.RazorpayPaymentId == razorpayPaymentId, ct);
            if (existing is not null)
            {
                order.Status = OrderStatus.Paid;
                order.PaymentId = existing.Id;
                order.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return existing.Id;
            }

            payment = await payments.RecordAsync(new PaymentRequest(
                order.StudentId, order.Amount, PaymentModes.Online, Clock.Today, "Paid online",
                order.FeeDues.Select(d => d.Id).ToList(), null, "Online (Razorpay)",
                razorpayPaymentId, AllowExtra: true), ct);

            order.Status = OrderStatus.Paid;
            order.PaymentId = payment.Id;
            order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            isNew = true;
        }

        if (isNew)
        {
            try
            {
                db.ChangeTracker.Clear();
                var p = await db.Payments.AsNoTracking()
                    .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
                    .FirstAsync(x => x.Id == payment.Id, ct);
                await messages.QueueReceiptAsync((await views.PaymentViews([p], ct))[0], ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Receipt message not queued for online payment {Payment}", payment.Id);
            }
        }
        return payment.Id;
    }

    public async Task MarkFailedAsync(string orderId, CancellationToken ct)
    {
        await db.PaymentOrders.IgnoreQueryFilters()
            .Where(o => o.RazorpayOrderId == orderId && o.Status == OrderStatus.Created)
            .ExecuteUpdateAsync(u => u
                .SetProperty(o => o.Status, OrderStatus.Failed)
                .SetProperty(o => o.UpdatedAt, DateTime.UtcNow), ct);
    }

    private void UseTenant(Guid tenantId)
    {
        if (current.TenantId is null) current.ActAsTenant(tenantId);
        else if (current.TenantId != tenantId) throw new InvalidOperationException("Order belongs to another centre.");
    }
}
