using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Payments;

public record RecordPaymentInput(
    string StudentId, int Amount, string Mode, string? PaidOn, string? Note,
    string[]? DueIds, bool? SendReceipt);

[ApiController]
[Route("api/v1/payments")]
[Authorize(Roles = "Owner,Staff")]
public class PaymentsController(
    ClassKendraContext db, CurrentUser current, PaymentService payments,
    FeeReadService views, MessageService messages, ILogger<PaymentsController> log) : ControllerBase
{
    /// <summary>GET /payments?from=&to=&mode=&batchId=&studentId= — defaults to this month.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PaymentView>>> List(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? mode,
        [FromQuery] Guid? batchId, [FromQuery] Guid? studentId, CancellationToken ct)
    {
        var today = Clock.Today;
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start) return BadRequest(new { message = "The end date is before the start date." });
        if (end.DayNumber - start.DayNumber > 400) return BadRequest(new { message = "Choose a range of one year or less." });

        var q = db.Payments.AsNoTracking()
            .Include(p => p.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .Where(p => p.PaidOn >= start && p.PaidOn <= end);
        if (!string.IsNullOrWhiteSpace(mode)) q = q.Where(p => p.Mode == mode);
        if (batchId is Guid b) q = q.Where(p => p.Student.BatchId == b);
        if (studentId is Guid s) q = q.Where(p => p.StudentId == s);

        var rows = await q.OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.ReceiptNo).ToListAsync(ct);
        return await views.PaymentViews(rows, ct);
    }

    /// <summary>GET /payments/{id} — one receipt.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentView>> Get(Guid id, CancellationToken ct)
    {
        var p = await db.Payments.AsNoTracking()
            .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { message = "Receipt not found." });
        return (await views.PaymentViews([p], ct))[0];
    }

    /// <summary>POST /payments — record a payment at the counter.</summary>
    [HttpPost]
    public async Task<ActionResult<PaymentView>> Record(RecordPaymentInput input, CancellationToken ct)
    {
        if (!Guid.TryParse(input.StudentId, out var studentId) ||
            !await db.Students.AnyAsync(s => s.Id == studentId, ct))
            return BadRequest(new { message = "Student not found." });
        if (!PaymentModes.Manual.Contains(input.Mode))
            return BadRequest(new { message = "Choose how the fee was paid: Cash, UPI or Cheque." });

        var paidOn = Clock.Today;
        if (!string.IsNullOrWhiteSpace(input.PaidOn) && !DateOnly.TryParse(input.PaidOn, out paidOn))
            return BadRequest(new { message = "Enter a valid payment date." });
        if (paidOn > Clock.Today) return BadRequest(new { message = "The payment date can't be in the future." });

        var dueIds = new List<Guid>();
        foreach (var raw in input.DueIds ?? [])
        {
            if (!Guid.TryParse(raw, out var g)) return BadRequest(new { message = "One of the chosen fees no longer exists." });
            if (!dueIds.Contains(g)) dueIds.Add(g);
        }

        var me = await db.Users.AsNoTracking().Where(u => u.Id == current.UserId)
            .Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";

        Guid paymentId;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                var payment = await payments.RecordAsync(new PaymentRequest(
                    studentId, input.Amount, input.Mode, paidOn, (input.Note ?? "").Trim(),
                    dueIds, current.UserId, me.Length > 0 ? me : current.Role), ct);
                await tx.CommitAsync(ct);
                paymentId = payment.Id;
            }
            catch (PaymentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        db.ChangeTracker.Clear();
        var view = (await Get(paymentId, ct)).Value!;

        if (input.SendReceipt != false)
        {
            try { await messages.QueueReceiptAsync(view, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Receipt message not queued for {Payment}", paymentId); }
        }
        return view;
    }

    /// <summary>DELETE /payments/{id} — owner only; online payments can't be deleted.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.Payments
            .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NoContent();
        if (p.RazorpayPaymentId is not null)
            return BadRequest(new { message = "Online payments can't be deleted here. Refund them from Razorpay." });

        await payments.DeleteAsync(p, ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }
}
