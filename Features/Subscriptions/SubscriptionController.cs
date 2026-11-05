using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.OnlinePayments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Subscriptions;

public record PlanDto(
    string Code, string Name, string InstitutionType, string StudentsLabel, int MaxStudents,
    int MonthlyPrice, int YearlyPrice, int MessageLimit, bool IsPopular);

public record SubscriptionStatus(
    string State, string PlanCode, string? PlanName, string? EndsOn, int DaysLeft,
    string? Cycle, int StudentCount, int? MaxStudents, int MessagesUsed, int MessageLimit);

public record CheckoutInput(string PlanCode, string Cycle);

/// <summary>What the Angular app passes to Razorpay Checkout.</summary>
public record CheckoutOrder(string OrderId, int AmountPaise, string Currency, string KeyId,
                            string Name, string Description, string? Phone);

public record VerifyInput(string RazorpayOrderId, string RazorpayPaymentId, string RazorpaySignature);

/// <summary>GET /plans — public price list (also usable by the marketing site).</summary>
[ApiController]
[Route("api/v1/plans")]
[AllowAnonymous]
public class PlansController(ClassKendraContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<PlanDto>>> List([FromQuery] string? institutionType, CancellationToken ct)
    {
        var q = db.Plans.AsNoTracking().Where(p => p.IsActive && p.MonthlyPrice > 0);
        if (!string.IsNullOrWhiteSpace(institutionType)) q = q.Where(p => p.InstitutionType == institutionType);
        return await q.OrderBy(p => p.SortOrder)
            .Select(p => new PlanDto(p.Code, p.Name, p.InstitutionType, p.StudentsLabel, p.MaxStudents,
                p.MonthlyPrice, p.YearlyPrice, p.MessageLimit, p.IsPopular))
            .ToListAsync(ct);
    }
}

/// <summary>
/// The centre's own ClassKendra plan. Plans are prepaid (monthly or yearly) through a Razorpay order;
/// the order id is kept in Subscriptions.RazorpaySubscriptionId. Renewing early adds time after the current end.
/// </summary>
[ApiController]
[Route("api/v1/subscription")]
[Authorize(Roles = "Owner,Staff")]
[SkipPlanCheck]
public class SubscriptionController(
    ClassKendraContext db, CurrentUser current, PlanService plans,
    SubscriptionService subscriptions, RazorpayClient razorpay) : ControllerBase
{
    /// <summary>GET /subscription — plan state, end date and limits.</summary>
    [HttpGet]
    public async Task<ActionResult<SubscriptionStatus>> Get(CancellationToken ct)
    {
        var plan = await plans.GetAsync(ct);
        if (plan is null) return NotFound(new { message = "Set up your centre first." });

        var t = await db.Tenants.AsNoTracking().Select(x => new { x.MessagesUsed, x.MessageLimit }).FirstAsync(ct);
        var students = await db.Students.CountAsync(s => s.IsActive, ct);
        var cycle = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == PlanStates.Active).OrderByDescending(s => s.CurrentPeriodEnd)
            .Select(s => s.Cycle).FirstOrDefaultAsync(ct);

        return new SubscriptionStatus(plan.State, plan.PlanCode, plan.PlanName,
            plan.EndsOn?.ToString("yyyy-MM-dd"),
            plan.EndsOn is DateOnly end ? Math.Max(0, end.DayNumber - Clock.Today.DayNumber) : 0,
            cycle, students, plan.MaxStudents, t.MessagesUsed, t.MessageLimit);
    }

    /// <summary>POST /subscription/checkout { planCode, cycle: Monthly|Yearly } → open Razorpay Checkout with the result.</summary>
    [HttpPost("checkout")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<CheckoutOrder>> Checkout(CheckoutInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        if (!razorpay.IsConfigured) return BadRequest(new { message = "Online payments are not switched on yet." });
        if (input.Cycle is not ("Monthly" or "Yearly")) return BadRequest(new { message = "Choose monthly or yearly." });

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == input.PlanCode && p.IsActive, ct);
        if (plan is null || plan.MonthlyPrice <= 0) return BadRequest(new { message = "That plan is not available." });

        var students = await db.Students.CountAsync(s => s.IsActive, ct);
        if (plan.MaxStudents > 0 && students > plan.MaxStudents)
            return BadRequest(new { message = $"You have {students:N0} students; {plan.Name} allows {plan.MaxStudents:N0}. Choose a bigger plan." });

        var amount = input.Cycle == "Yearly" ? plan.YearlyPrice : plan.MonthlyPrice;
        var tenant = await db.Tenants.AsNoTracking().Select(t => new { t.Name }).FirstAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == current.UserId).Select(u => u.Phone).FirstOrDefaultAsync(ct);

        var orderId = await razorpay.CreateOrderAsync(amount, $"sub_{tenantId:N}"[..20],
            new() { ["kind"] = "subscription", ["tenantId"] = tenantId.ToString(), ["plan"] = plan.Code }, ct);

        db.Subscriptions.Add(new Subscription
        {
            TenantId = tenantId,
            PlanCode = plan.Code,
            Cycle = input.Cycle,
            Amount = amount,
            Status = SubscriptionService.Created,
            RazorpaySubscriptionId = orderId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return new CheckoutOrder(orderId, amount * 100, "INR", razorpay.KeyId, "ClassKendra",
            $"{plan.Name} plan · {input.Cycle.ToLowerInvariant()} · {tenant.Name}", owner);
    }

    /// <summary>POST /subscription/verify — called by Angular after Razorpay Checkout succeeds.</summary>
    [HttpPost("verify")]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<SubscriptionStatus>> Verify(VerifyInput input, CancellationToken ct)
    {
        if (!razorpay.CheckoutSignatureValid(input.RazorpayOrderId, input.RazorpayPaymentId, input.RazorpaySignature))
            return BadRequest(new { message = "We couldn't confirm this payment. If money was taken, it will show here within a few minutes." });

        if (!await subscriptions.ActivateAsync(input.RazorpayOrderId, ct))
            return BadRequest(new { message = "Plan payment not found." });

        plans.Reset();
        return await Get(ct);
    }
}

public class SubscriptionService(ClassKendraContext db, CurrentUser current)
{
    public const string Created = "Created";

    /// <summary>Turns a paid plan order into an active plan. Safe to call twice (callback + webhook).</summary>
    public async Task<bool> ActivateAsync(string orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sub = await db.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM Subscriptions WITH (UPDLOCK, ROWLOCK) WHERE RazorpaySubscriptionId = {orderId}")
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
        var start = lastEnd is DateOnly e && e >= today ? e.AddDays(1) : today;

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
