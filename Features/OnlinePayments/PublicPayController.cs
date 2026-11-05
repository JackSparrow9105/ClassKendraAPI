using System.Text;
using System.Text.Json.Nodes;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Subscriptions;
using ClassKendraAPI.Features.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.OnlinePayments;

// Match ParentPayPage and ParentPayOrder in models.ts.
public record ParentPayPage(
    string OrgName, string OrgCity, string StudentName, string BatchName,
    List<FeeDueView> Dues, int Total, string Language);
public record ParentPayOrder(string OrderId, int Amount, string KeyId);
public record PublicOrderInput(string[]? DueIds);
public record VerifyInput(string RazorpayOrderId, string RazorpayPaymentId, string RazorpaySignature);

/// <summary>
/// The parent's payment page: {App:PublicUrl}/pay/{token}. No login — the long random token is the key.
///  1. GET  /public/pay/{token}                   → page data
///  2. POST /public/pay/{token}/order { dueIds }  → Razorpay order; the page opens Razorpay Checkout
///  3. POST /public/pay/{token}/verify            → called by Checkout's success handler; records the payment, returns the receipt
///  4. GET  /public/pay/{token}/orders/{orderId}  → the receipt (404 until paid) — fallback if step 3 failed and the webhook recorded it
/// </summary>
[ApiController]
[Route("api/v1/public/pay/{token}")]
[AllowAnonymous]
[EnableRateLimiting("public")]
public class PublicPayController(
    ClassKendraContext db, CurrentUser current, RazorpayClient razorpay, OnlinePaymentService online,
    FeeReadService views, ILogger<PublicPayController> log) : ControllerBase
{
    private const string Invalid = "This payment link is not valid. Please ask the school for a new one.";

    [HttpGet]
    public async Task<ActionResult<ParentPayPage>> Get(string token, CancellationToken ct)
    {
        var student = await FindStudent(token, ct);
        if (student is null) return NotFound(new { message = Invalid });

        var today = Clock.Today;
        var dues = (await UnpaidDues(student.Id, ct))
            .Select(d => FeeViews.ToView(d, student.Name, d.Batch.Name, today))
            .ToList();

        return new ParentPayPage(student.Tenant.Name, student.Tenant.City, student.Name, student.Batch.Name,
            dues, dues.Sum(d => d.Balance), (student.Tenant.MessageLanguage ?? "en").Trim());
    }

    /// <summary>POST /public/pay/{token}/order { dueIds } → { orderId, amount, keyId }</summary>
    [HttpPost("order")]
    public async Task<ActionResult<ParentPayOrder>> CreateOrder(string token, PublicOrderInput input, CancellationToken ct)
    {
        var student = await FindStudent(token, ct);
        if (student is null) return NotFound(new { message = Invalid });
        if (!razorpay.IsConfigured)
            return BadRequest(new { message = "Online payment is not available yet. Please pay at the school." });

        var dues = await UnpaidDues(student.Id, ct);
        var wanted = (input.DueIds ?? []).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).ToHashSet();
        var chosen = wanted.Count == 0 ? dues : dues.Where(d => wanted.Contains(d.Id)).ToList();
        if (chosen.Count == 0 || (wanted.Count > 0 && chosen.Count != wanted.Count))
            return BadRequest(new { message = "Some of these fees are already paid. Please refresh the page." });

        var amount = chosen.Sum(Balance);
        if (amount <= 0) return BadRequest(new { message = "Nothing is due right now." });

        string orderId;
        try
        {
            orderId = await razorpay.CreateOrderAsync(amount, $"fee_{student.Id:N}"[..20], new()
            {
                ["kind"] = "fees",
                ["tenantId"] = student.TenantId.ToString(),
                ["studentId"] = student.Id.ToString()
            }, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            log.LogError(ex, "Razorpay order failed for student {Student}", student.Id);
            return StatusCode(502, new { message = "Online payment is not working right now. Please try again in a few minutes." });
        }

        var order = new PaymentOrder
        {
            TenantId = student.TenantId,
            StudentId = student.Id,
            RazorpayOrderId = orderId,
            Amount = amount,
            Status = OrderStatus.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        foreach (var d in chosen) order.FeeDues.Add(d);
        db.PaymentOrders.Add(order);
        await db.SaveChangesAsync(ct);

        return new ParentPayOrder(orderId, amount, razorpay.KeyId);
    }

    /// <summary>POST /public/pay/{token}/verify — Razorpay Checkout's success handler. Returns the receipt.</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<PaymentView>> Verify(string token, VerifyInput input, CancellationToken ct)
    {
        var student = await FindStudent(token, ct);
        if (student is null) return NotFound(new { message = Invalid });

        if (!razorpay.CheckoutSignatureValid(input.RazorpayOrderId, input.RazorpayPaymentId, input.RazorpaySignature))
            return BadRequest(new { message = "We couldn't confirm this payment yet. If money was taken, the receipt will reach you on WhatsApp shortly." });

        if (!await db.PaymentOrders.AnyAsync(o => o.RazorpayOrderId == input.RazorpayOrderId && o.StudentId == student.Id, ct))
            return BadRequest(new { message = "This payment is for a different student." });

        var paymentId = await online.CompleteFeeOrderAsync(input.RazorpayOrderId, input.RazorpayPaymentId, ct);
        if (paymentId is null) return BadRequest(new { message = "Payment not found." });
        return await Receipt(paymentId.Value, ct);
    }

    /// <summary>GET /public/pay/{token}/orders/{orderId} — the receipt once the payment is recorded; 404 before that.</summary>
    [HttpGet("orders/{orderId}")]
    public async Task<ActionResult<PaymentView>> Order(string token, string orderId, CancellationToken ct)
    {
        var student = await FindStudent(token, ct);
        if (student is null) return NotFound(new { message = Invalid });

        var paymentId = await db.PaymentOrders.AsNoTracking()
            .Where(o => o.RazorpayOrderId == orderId && o.StudentId == student.Id && o.Status == OrderStatus.Paid)
            .Select(o => o.PaymentId)
            .FirstOrDefaultAsync(ct);
        if (paymentId is null) return NotFound(new { message = "Payment not received yet." });
        return await Receipt(paymentId.Value, ct);
    }

    // ---------- helpers ----------

    private async Task<PaymentView> Receipt(Guid paymentId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var p = await db.Payments.AsNoTracking()
            .Include(x => x.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .FirstAsync(x => x.Id == paymentId, ct);
        return (await views.PaymentViews([p], ct))[0];
    }

    /// <summary>Active students only (a student who left gets "link not valid", as in the app).</summary>
    private async Task<Student?> FindStudent(string token, CancellationToken ct)
    {
        if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return null;

        var student = await db.Students.IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Tenant).Include(s => s.Batch)
            .FirstOrDefaultAsync(s => s.PayToken == token && s.IsActive, ct);
        if (student is not null) current.ActAsTenant(student.TenantId);   // everything after this sees only that centre
        return student;
    }

    private Task<List<FeeDue>> UnpaidDues(Guid studentId, CancellationToken ct) =>
        db.FeeDues
            .Include(d => d.Batch)
            .Where(d => d.StudentId == studentId && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial))
            .OrderBy(d => d.DueDate).ThenBy(d => d.HeadName)
            .ToListAsync(ct);

    private static int Balance(FeeDue d) => (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount;
}

/// <summary>
/// Razorpay webhook → /api/v1/webhooks/razorpay. In the Razorpay dashboard enable:
/// payment.captured, order.paid, payment.failed, payment_link.paid. Set Razorpay:WebhookSecret to the same secret.
/// This is the safety net when the parent closes the browser before the success callback.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/razorpay")]
[AllowAnonymous]
public class RazorpayWebhookController(
    RazorpayClient razorpay, OnlinePaymentService online, SubscriptionService subscriptions,
    ILogger<RazorpayWebhookController> log) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        if (!razorpay.WebhookSignatureValid(body, Request.Headers["X-Razorpay-Signature"].ToString()))
            return Unauthorized();

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return Ok(); }

        var evt = root?["event"]?.GetValue<string>();
        var payment = root?["payload"]?["payment"]?["entity"];
        var orderId = payment?["order_id"]?.GetValue<string>() ?? root?["payload"]?["order"]?["entity"]?["id"]?.GetValue<string>();
        var paymentId = payment?["id"]?.GetValue<string>();
        if (orderId is null && evt != "payment_link.paid") return Ok();

        try
        {
            switch (evt)
            {
                case "payment.captured" or "order.paid" when paymentId is not null && orderId is not null:
                    // Plan payments arrive as payment_link.paid; here only parents' fee orders matter.
                    // Razorpay sends notes as [] when empty, so check it is an object first.
                    var kind = (payment?["notes"] as JsonObject)?["kind"]?.GetValue<string>();
                    if (kind != "subscription") await online.CompleteFeeOrderAsync(orderId, paymentId, ct);
                    break;
                case "payment_link.paid":
                    var linkId = root?["payload"]?["payment_link"]?["entity"]?["id"]?.GetValue<string>();
                    if (linkId is not null) await subscriptions.ActivateAsync(linkId, ct);
                    break;
                case "payment.failed" when orderId is not null:
                    await online.MarkFailedAsync(orderId, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 500 makes Razorpay retry later.
            log.LogError(ex, "Razorpay webhook {Event} for order {Order} failed", evt, orderId);
            return StatusCode(500);
        }
        return Ok();
    }
}
