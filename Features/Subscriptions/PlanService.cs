using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Subscriptions;

public static class PlanStates
{
    public const string Trial = "Trial";
    public const string Active = "Active";
    public const string Expired = "Expired";
    /// <summary>Tuition / coaching centres whose trial or paid plan has ended: the free-forever plan.</summary>
    public const string Free = "Free";

    /// <summary>Plans.Code of the free plan.</summary>
    public const string FreePlanCode = "Free";
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
            .Select(t => new { t.PlanCode, t.TrialEndsOn, t.InstitutionType })
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

        // Tuition / coaching centres don't get locked: they move to the free-forever plan.
        if (tenant.InstitutionType != "School")
        {
            var free = await db.Plans.AsNoTracking()
                .Where(p => p.Code == PlanStates.FreePlanCode && p.IsActive)
                .Select(p => new { p.Name, p.MaxStudents, p.MessageLimit })
                .FirstOrDefaultAsync(ct);
            if (free is not null)
            {
                // First time we notice: put the centre on the Free plan and its message limit.
                if (tenant.PlanCode != PlanStates.FreePlanCode)
                    await db.Tenants.ExecuteUpdateAsync(u => u
                        .SetProperty(t => t.PlanCode, PlanStates.FreePlanCode)
                        .SetProperty(t => t.MessageLimit, free.MessageLimit), ct);
                return _cached = new PlanState(PlanStates.Free, PlanStates.FreePlanCode, free.Name, null, free.MaxStudents);
            }
        }

        return _cached = new PlanState(PlanStates.Expired, sub?.PlanCode ?? tenant.PlanCode, sub?.Name ?? trialPlan?.Name,
            sub?.CurrentPeriodEnd ?? tenant.TrialEndsOn, sub?.MaxStudents ?? trialPlan?.MaxStudents);
    }

    /// <summary>Can the centre add and change records (and send automatic reminders)?</summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct) =>
        (await GetAsync(ct))?.State switch
        {
            PlanStates.Trial or PlanStates.Active => true,
            PlanStates.Free => await FreeLimitErrorAsync(ct) is null,
            _ => false
        };

    public async Task<bool> IsFreeAsync(CancellationToken ct) => (await GetAsync(ct))?.State == PlanStates.Free;

    /// <summary>
    /// On the Free plan with more active students than it allows (e.g. 120 students left from the trial):
    /// the reason to show. Null when within the limit or not on the Free plan.
    /// </summary>
    public async Task<string?> FreeLimitErrorAsync(CancellationToken ct)
    {
        var plan = await GetAsync(ct);
        if (plan?.State != PlanStates.Free || plan.MaxStudents is not int max || max <= 0) return null;
        var count = await db.Students.CountAsync(s => s.IsActive, ct);
        return count <= max
            ? null
            : $"The Free plan is for up to {max:N0} students and you have {count:N0}. Choose a plan to keep adding fees and attendance — or remove students who have left.";
    }

    public const string FreeOwnerOnlyMessage =
        "This centre is on the Free plan, which has the owner login only. Ask the owner to choose a plan to use the app.";

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
