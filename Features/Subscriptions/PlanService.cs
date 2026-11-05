using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Subscriptions;

public static class PlanStates
{
    public const string Trial = "Trial";
    public const string Active = "Active";
    public const string Expired = "Expired";
}

public record PlanState(string State, string PlanCode, string? PlanName, DateOnly? EndsOn, int? MaxStudents);

/// <summary>Is the centre on a running trial or paid plan, and what are its limits?</summary>
public class PlanService(ClassKendraContext db)
{
    /// <summary>Days after the trial/plan end when the centre can still add records.</summary>
    public const int GraceDays = 3;

    private PlanState? _cached;

    /// <summary>Forget the cached answer (after the plan changes in this request).</summary>
    public void Reset() => _cached = null;

    public async Task<PlanState?> GetAsync(CancellationToken ct)
    {
        if (_cached is not null) return _cached;

        var tenant = await db.Tenants.AsNoTracking()
            .Select(t => new { t.PlanCode, t.TrialEndsOn })
            .FirstOrDefaultAsync(ct);
        if (tenant is null) return null;

        var today = Clock.Today;
        var sub = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == PlanStates.Active && s.CurrentPeriodEnd != null)
            .OrderByDescending(s => s.CurrentPeriodEnd)
            .Select(s => new { s.PlanCode, s.CurrentPeriodEnd, s.PlanCodeNavigation.Name, s.PlanCodeNavigation.MaxStudents })
            .FirstOrDefaultAsync(ct);

        if (sub is not null && sub.CurrentPeriodEnd!.Value.AddDays(GraceDays) >= today)
            return _cached = new PlanState(PlanStates.Active, sub.PlanCode, sub.Name, sub.CurrentPeriodEnd, sub.MaxStudents);

        var trialPlan = await db.Plans.AsNoTracking().Where(p => p.Code == tenant.PlanCode)
            .Select(p => new { p.Name, p.MaxStudents }).FirstOrDefaultAsync(ct);

        if (tenant.TrialEndsOn.AddDays(GraceDays) >= today && sub is null)
            return _cached = new PlanState(PlanStates.Trial, tenant.PlanCode, trialPlan?.Name ?? "Free trial",
                tenant.TrialEndsOn, trialPlan?.MaxStudents);

        return _cached = new PlanState(PlanStates.Expired, sub?.PlanCode ?? tenant.PlanCode, sub?.Name ?? trialPlan?.Name,
            sub?.CurrentPeriodEnd ?? tenant.TrialEndsOn, sub?.MaxStudents ?? trialPlan?.MaxStudents);
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct) =>
        (await GetAsync(ct))?.State is PlanStates.Trial or PlanStates.Active;

    /// <summary>Null when the centre can add this many more students.</summary>
    public async Task<string?> StudentLimitErrorAsync(int adding, CancellationToken ct)
    {
        var plan = await GetAsync(ct);
        if (plan?.MaxStudents is not int max || max <= 0) return null;

        var count = await db.Students.CountAsync(s => s.IsActive, ct);
        if (count + adding <= max) return null;

        var room = Math.Max(0, max - count);
        return room == 0
            ? $"Your {plan.PlanName} plan allows {max:N0} students and you have {count:N0}. Upgrade to add more."
            : $"Your {plan.PlanName} plan allows {max:N0} students — you can add {room:N0} more. Upgrade to add all of them.";
    }
}
