using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Activity;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using ClassKendraAPI.Features.StaffAttendance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Payments;

/// <param name="KeepExtra">true = money beyond the ticked fees goes on the student's other unpaid fees, and what is left is kept as advance.</param>
public record RecordPaymentInput(
    string StudentId, int Amount, string Mode, string? PaidOn, string? Note,
    string[]? DueIds, bool? SendReceipt, bool? KeepExtra = null);

/// <summary>One child's ticked fees in a family payment.</summary>
public record FamilyPaymentItem(string StudentId, string[]? DueIds);

/// <summary>
/// POST /payments/family — one amount for brothers and sisters together. It is spread over all the ticked fees
/// oldest first; each child gets their own receipt, linked together.
/// </summary>
public record FamilyPaymentInput(List<FamilyPaymentItem> Items, int Amount, string Mode, string? PaidOn, string? Note, bool? SendReceipt);

public record CancelPaymentInput(string? Reason);
public record RestorePaymentInput(string? Reason);

/// <summary>One page of receipts plus the total of ALL receipts that match (not only this page). Cancelled ones are never added up.</summary>
public record PaymentPage(List<PaymentView> Items, int Total, int Page, int PageSize, int TotalAmount);

[ApiController]
[Route("api/v1/payments")]
[Authorize(Roles = "Owner,Staff")]
public class PaymentsController(
    ClassKendraContext db, CurrentUser current, PaymentService payments,
    FeeReadService views, MessageService messages, AuditService audit, PushService push,
    AdvanceService advances, ILogger<PaymentsController> log) : ControllerBase
{
    private const int MinReasonLength = 10;

    /// <summary>GET /payments?from=&to=&mode=&batchId=&studentId= — everything in the range (defaults to this month). For long lists use /payments/page.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PaymentView>>> List(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? mode,
        [FromQuery] Guid? batchId, [FromQuery] Guid? studentId, CancellationToken ct)
    {
        var (q, error) = Filter(db.Payments.AsNoTracking(), from, to, mode, batchId, studentId, null);
        if (error is not null) return BadRequest(new { message = error });

        var rows = await q!.Include(p => p.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.ReceiptNo).ToListAsync(ct);
        return await views.PaymentViews(rows, ct);
    }

    /// <summary>
    /// GET /payments/page?from=&to=&mode=&batchId=&studentId=&search=&includeCancelled=&page=&pageSize=
    /// Newest first, 50 at a time. search = receipt number or student name.
    /// includeCancelled=true also lists cancelled receipts (marked cancelled; never part of totalAmount).
    /// </summary>
    [HttpGet("page")]
    public async Task<ActionResult<PaymentPage>> Page(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? mode,
        [FromQuery] Guid? batchId, [FromQuery] Guid? studentId, [FromQuery] string? search,
        [FromQuery] bool includeCancelled, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var (p, size, skip) = Paging.Clean(page, pageSize);

        var source = includeCancelled
            ? db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled]).AsNoTracking()
            : db.Payments.AsNoTracking();
        var (q, error) = Filter(source, from, to, mode, batchId, studentId, search);
        if (error is not null) return BadRequest(new { message = error });

        var total = await q!.CountAsync(ct);
        var totalAmount = await q!.Where(x => x.CancelledAt == null).SumAsync(x => (int?)x.Amount, ct) ?? 0;
        var rows = await q!.Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .OrderByDescending(x => x.PaidOn).ThenByDescending(x => x.ReceiptNo)
            .Skip(skip).Take(size).ToListAsync(ct);

        return new PaymentPage(await views.PaymentViews(rows, ct), total, p, size, totalAmount);
    }

    private static (IQueryable<Payment>? Query, string? Error) Filter(
        IQueryable<Payment> q, DateOnly? from, DateOnly? to, string? mode, Guid? batchId, Guid? studentId, string? search)
    {
        var today = Clock.Today;
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start) return (null, "The end date is before the start date.");
        if (end.DayNumber - start.DayNumber > 400) return (null, "Choose a range of one year or less.");

        q = q.Where(p => p.PaidOn >= start && p.PaidOn <= end);
        q = q.Where(p => p.Mode != PaymentModes.Opening);   // advance from before ClassKendra isn't a receipt of this period
        if (!string.IsNullOrWhiteSpace(mode)) q = q.Where(p => p.Mode == mode);
        if (batchId is Guid b) q = q.Where(p => p.Student.BatchId == b);
        if (studentId is Guid s) q = q.Where(p => p.StudentId == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(p => p.ReceiptNo.Contains(term) || p.Student.Name.Contains(term));
        }
        return (q, null);
    }

    /// <summary>GET /payments/{id} — one receipt (a cancelled one too, marked cancelled).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentView>> Get(Guid id, CancellationToken ct)
    {
        var p = await db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled]).AsNoTracking()
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
                    dueIds, current.UserId, me.Length > 0 ? me : current.Role, KeepExtra: input.KeepExtra == true), ct);
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

        // SendReceipt: true = send, false = don't, missing = the centre's setting.
        if (input.SendReceipt != false)
        {
            try { await messages.QueueReceiptAsync(view, ct, force: input.SendReceipt == true); }
            catch (Exception ex) { log.LogWarning(ex, "Receipt message not queued for {Payment}", paymentId); }
        }
        return view;
    }

    /// <summary>
    /// POST /payments/family { items: [{ studentId, dueIds }], amount, mode, paidOn, note, sendReceipt } → PaymentView[]
    /// Brothers and sisters (same parent phone) paying together at the counter. The amount is spread over all
    /// the ticked fees, oldest due date first, so a part payment clears the oldest fees of any child first.
    /// Each child gets a receipt for their part; the receipts are linked ("paid together").
    /// </summary>
    [HttpPost("family")]
    public async Task<ActionResult<List<PaymentView>>> RecordFamily(FamilyPaymentInput input, CancellationToken ct)
    {
        if (!PaymentModes.Manual.Contains(input.Mode))
            return BadRequest(new { message = "Choose how the fee was paid: Cash, UPI or Cheque." });
        if (input.Amount <= 0) return BadRequest(new { message = "Enter an amount above zero." });
        var paidOn = Clock.Today;
        if (!string.IsNullOrWhiteSpace(input.PaidOn) && !DateOnly.TryParse(input.PaidOn, out paidOn))
            return BadRequest(new { message = "Enter a valid payment date." });
        if (paidOn > Clock.Today) return BadRequest(new { message = "The payment date can't be in the future." });

        var wanted = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var item in input.Items ?? [])
        {
            if (!Guid.TryParse(item.StudentId, out var sid)) return BadRequest(new { message = "Student not found." });
            var dues = new HashSet<Guid>();
            foreach (var raw in item.DueIds ?? [])
            {
                if (!Guid.TryParse(raw, out var g)) return BadRequest(new { message = "One of the chosen fees no longer exists." });
                dues.Add(g);
            }
            if (dues.Count > 0) wanted[sid] = dues;
        }
        if (wanted.Count == 0) return BadRequest(new { message = "Tick the fees this payment is for." });

        var students = await db.Students.AsNoTracking().Where(s => wanted.Keys.Contains(s.Id))
            .Select(s => new { s.Id, s.ParentPhone }).ToListAsync(ct);
        if (students.Count != wanted.Count) return BadRequest(new { message = "Student not found." });
        if (students.Select(s => s.ParentPhone).Distinct().Count() > 1)
            return BadRequest(new { message = "These students aren't one family (different parent phone numbers)." });

        // Spread the amount over every ticked fee, oldest first, to see what each child pays.
        var allIds = wanted.Values.SelectMany(x => x).ToList();
        var bills = await db.FeeDues.AsNoTracking()
            .Where(d => allIds.Contains(d.Id) && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial))
            .Select(d => new { d.Id, d.StudentId, d.DueDate, d.HeadName, Balance = d.GrossAmount - d.Discount - d.PaidAmount })
            .ToListAsync(ct);
        if (bills.Count != allIds.Count || bills.Any(b => !wanted.TryGetValue(b.StudentId, out var set) || !set.Contains(b.Id)))
            return BadRequest(new { message = "One of the ticked fees is already paid or was removed. Reload and try again." });
        var owed = bills.Sum(b => b.Balance);
        if (input.Amount > owed) return BadRequest(new { message = $"That's more than the {Money.Inr(owed)} due for the ticked fees." });

        var share = new Dictionary<Guid, int>();
        var left = input.Amount;
        foreach (var b in bills.OrderBy(b => b.DueDate).ThenBy(b => b.HeadName))
        {
            if (left == 0) break;
            var take = Math.Min(left, b.Balance);
            share[b.StudentId] = share.GetValueOrDefault(b.StudentId) + take;
            left -= take;
        }

        var me = await audit.UserNameAsync(ct);
        var groupId = Guid.NewGuid();
        var ids = new List<Guid>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                foreach (var (sid, amount) in share.Where(x => x.Value > 0))
                {
                    var payment = await payments.RecordAsync(new PaymentRequest(
                        sid, amount, input.Mode, paidOn, (input.Note ?? "").Trim(),
                        wanted[sid].ToList(), current.UserId, me, GroupId: share.Count(x => x.Value > 0) > 1 ? groupId : null), ct);
                    ids.Add(payment.Id);
                }
                await tx.CommitAsync(ct);
            }
            catch (PaymentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        db.ChangeTracker.Clear();
        var rows = await db.Payments.AsNoTracking()
            .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var result = (await views.PaymentViews(rows, ct)).OrderBy(v => v.ReceiptNo).ToList();

        if (input.SendReceipt != false)
            foreach (var view in result)
            {
                try { await messages.QueueReceiptAsync(view, ct, force: input.SendReceipt == true); }
                catch (Exception ex) { log.LogWarning(ex, "Receipt message not queued for {Payment}", view.Id); }
            }
        return result;
    }

    /// <summary>
    /// POST /payments/{id}/cancel { reason } → PaymentView (cancelled: true)
    /// Owner or office staff. The reason is required and goes in the activity log; when staff cancel,
    /// the owner gets an app notification. The payment is not deleted and its receipt number is not reused.
    /// Online payments can't be cancelled here.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<PaymentView>> Cancel(Guid id, CancelPaymentInput input, CancellationToken ct)
    {
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < MinReasonLength)
            return BadRequest(new { message = $"Write why this payment is being cancelled (at least {MinReasonLength} letters)." });
        if (reason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 letters." });

        var p = await db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled])
            .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { message = "Receipt not found." });
        if (p.CancelledAt is not null) return await Get(id, ct);   // already cancelled: nothing more to do
        if (p.RazorpayPaymentId is not null || p.Mode == PaymentModes.Online)
            return BadRequest(new { message = "Online payments can't be cancelled here. Refund them from Razorpay." });
        if (p.Mode == PaymentModes.Opening)
            return BadRequest(new { message = "This is the advance from before ClassKendra. Change it in the student's opening balance." });

        // An old payment may not have its receipt frozen yet: freeze it now, while its fees are still attached.
        var before = (await views.PaymentViews([p], ct))[0];
        var me = await audit.UserNameAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            List<CancelledAllocation> undone;
            try { undone = await payments.CancelAsync(p, reason, me, ct); }
            catch (PaymentException ex) { return BadRequest(new { message = ex.Message }); }
            await audit.AddAsync(AuditActions.PaymentCancelled, "Payment", p.Id,
                $"Cancelled receipt {p.ReceiptNo} ({Money.Inr(p.Amount)}, {p.Mode}) of {before.StudentName}",
                reason,
                new { p.ReceiptNo, p.Amount, p.Mode, PaidOn = p.PaidOn.ToString("yyyy-MM-dd"), p.StudentId, Allocations = undone },
                ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Staff cancelled it: tell the owner on their phone (free app notification; no WhatsApp).
        if (current.Role != "Owner")
        {
            try
            {
                var owners = await db.Users.AsNoTracking().Where(u => u.Role == "Owner" && u.IsActive).Select(u => u.Id).ToListAsync(ct);
                foreach (var owner in owners)
                    await push.SendAsync(owner, "Payment cancelled",
                        $"{me} cancelled receipt {p.ReceiptNo} ({Money.Inr(p.Amount)}) of {before.StudentName}. Reason: {reason}",
                        $"/receipts/{p.Id}", ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "Owner not notified about cancelled payment {Payment}", p.Id); }
        }

        db.ChangeTracker.Clear();
        return await Get(id, ct);
    }

    /// <summary>
    /// POST /payments/{id}/restore { reason } → PaymentView — owner only. Undoes a cancellation: the payment counts again
    /// with the same receipt number. Its money goes back on the student's unpaid fees, oldest first (which may not be
    /// exactly the fees it was first for, if some were paid meanwhile); anything left over is kept as advance.
    /// </summary>
    [HttpPost("{id:guid}/restore")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<PaymentView>> Restore(Guid id, RestorePaymentInput input, CancellationToken ct)
    {
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < MinReasonLength)
            return BadRequest(new { message = $"Write why this payment is being restored (at least {MinReasonLength} letters)." });
        if (reason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 letters." });

        var p = await db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled]).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { message = "Receipt not found." });
        if (p.CancelledAt is null) return await Get(id, ct);   // not cancelled: nothing to do
        var studentName = await db.Students.Where(s => s.Id == p.StudentId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";

        var wasReason = p.CancelReason;
        p.CancelledAt = null;
        p.CancelledByUserId = null;
        p.CancelledByName = null;
        p.CancelReason = null;
        await audit.AddAsync(AuditActions.PaymentRestored, "Payment", p.Id,
            $"Restored receipt {p.ReceiptNo} ({Money.Inr(p.Amount)}, {p.Mode}) of {studentName}", reason,
            new { p.ReceiptNo, p.Amount, WasCancelledFor = wasReason }, ct);
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        await advances.ApplyAsync([p.StudentId], ct);   // the money goes back on unpaid fees, oldest first
        db.ChangeTracker.Clear();
        return await Get(id, ct);
    }
}
