using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.OnlinePayments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Subscriptions;

// Matches Plan in models.ts.
public record PlanDto(string Code, string Name, string Students, int Monthly, int Yearly, int Messages, bool? Popular);
public record CheckoutInput(string Plan, string Cycle);
public record CheckoutResponse(string PaymentUrl);

/// <summary>
/// The centre's own ClassKendra plan. Prices live in the Plans table so they can change without a release.
/// Checkout = a Razorpay Payment Link; when it is paid, the Razorpay webhook (payment_link.paid)
/// switches the plan on. The link id is kept in Subscriptions.RazorpaySubscriptionId.
/// </summary>
[ApiController]
[Route("api/v1/billing")]
[Authorize(Roles = "Owner")]
[SkipPlanCheck]
public class BillingController(ClassKendraContext db, CurrentUser current, RazorpayClient razorpay,
                               IConfiguration config, ILogger<BillingController> log) : ControllerBase
{
    /// <summary>GET /billing/plans — coaching or school plans, for the signed-in centre.</summary>
    [HttpGet("plans")]
    public async Task<ActionResult<List<PlanDto>>> Plans(CancellationToken ct)
    {
        var type = await db.Tenants.AsNoTracking().Select(t => t.InstitutionType).FirstOrDefaultAsync(ct) ?? "Coaching";
        return await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && p.Code != "Trial" && p.InstitutionType == type)
            .OrderBy(p => p.SortOrder)
            .Select(p => new PlanDto(p.Code, p.Name, p.StudentsLabel, p.MonthlyPrice, p.YearlyPrice, p.MessageLimit,
                p.IsPopular ? true : null))
            .ToListAsync(ct);
    }

    /// <summary>POST /billing/checkout { plan, cycle: monthly|yearly } → { paymentUrl } (the app redirects there).</summary>
    [HttpPost("checkout")]
    public async Task<ActionResult<CheckoutResponse>> Checkout(CheckoutInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!razorpay.IsConfigured) return BadRequest(new { message = "Online payment is not switched on yet. Please contact us on WhatsApp." });

        var cycle = input.Cycle?.ToLowerInvariant() switch { "monthly" => "Monthly", "yearly" => "Yearly", _ => null };
        if (cycle is null) return BadRequest(new { message = "Choose monthly or yearly." });

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == input.Plan && p.IsActive, ct);
        if (plan is null || plan.Code == "Trial") return BadRequest(new { message = "That plan is not available." });

        var students = await db.Students.CountAsync(s => s.IsActive, ct);
        if (plan.MaxStudents > 0 && students > plan.MaxStudents)
            return BadRequest(new { message = $"You have {students:N0} students; {plan.Name} allows {plan.MaxStudents:N0}. Choose a bigger plan." });

        var amount = cycle == "Yearly" ? plan.YearlyPrice : plan.MonthlyPrice;
        var tenant = await db.Tenants.AsNoTracking().Select(t => new { t.Name }).FirstAsync(ct);
        var phone = await db.Users.AsNoTracking().Where(u => u.Id == current.UserId).Select(u => u.Phone).FirstOrDefaultAsync(ct);
        var back = $"{(config["App:PublicUrl"] ?? "").TrimEnd('/')}/plans";

        PaymentLink link;
        try
        {
            link = await razorpay.CreatePaymentLinkAsync(amount,
                $"ClassKendra {plan.Name} plan ({cycle.ToLowerInvariant()}) · {tenant.Name}", phone, back,
                new() { ["kind"] = "subscription", ["tenantId"] = tenantId.ToString(), ["plan"] = plan.Code }, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            log.LogError(ex, "Razorpay payment link failed for tenant {Tenant}", tenantId);
            return StatusCode(502, new { message = "Online payment is not working right now. Please try again in a few minutes." });
        }

        db.Subscriptions.Add(new Subscription
        {
            TenantId = tenantId,
            PlanCode = plan.Code,
            Cycle = cycle,
            Amount = amount,
            Status = SubscriptionService.Created,
            RazorpaySubscriptionId = link.Id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return new CheckoutResponse(link.ShortUrl);
    }
}

public class SubscriptionService(ClassKendraContext db, CurrentUser current)
{
    public const string Created = "Created";

    /// <summary>Turns a paid plan link into an active plan. Safe to call twice.</summary>
    public async Task<bool> ActivateAsync(string razorpayId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sub = await db.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM Subscriptions WITH (UPDLOCK, ROWLOCK) WHERE RazorpaySubscriptionId = {razorpayId}")
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct);
        if (sub is null) return false;

        if (current.TenantId is null) current.ActAsTenant(sub.TenantId);
        else if (current.TenantId != sub.TenantId) return false;

        if (sub.Status == PlanStates.Active) return true;

        var today = Clock.Today;
        var lastEnd = await db.Subscriptions
            .Where(s => s.Status == PlanStates.Active && s.CurrentPeriodEnd != null)
            .MaxAsync(s => s.CurrentPeriodEnd, ct);
        var start = lastEnd is DateOnly e && e >= today ? e.AddDays(1) : today;   // renewing early adds time

        sub.Status = PlanStates.Active;
        sub.CurrentPeriodStart = start;
        sub.CurrentPeriodEnd = start.AddMonths(sub.Cycle == "Yearly" ? 12 : 1).AddDays(-1);

        var plan = await db.Plans.FirstAsync(p => p.Code == sub.PlanCode, ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == sub.TenantId, ct);
        tenant.PlanCode = plan.Code;
        tenant.MessageLimit = plan.MessageLimit;
        tenant.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
