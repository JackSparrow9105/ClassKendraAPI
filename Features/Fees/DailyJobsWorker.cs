using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

/// <summary>
/// Once a day for every centre:
///  1. raise this month's bills (safe to repeat — catches new months, new students, missed runs)
///  2. after 10:00 IST, send automatic fee reminders (only while the centre's plan is active)
/// Checks every 30 minutes; each job runs once per IST day per app start (and both are safe to repeat).
/// </summary>
public class DailyJobsWorker(IServiceScopeFactory scopes, ILogger<DailyJobsWorker> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private const int ReminderHour = 10;

    private DateOnly _billedOn, _remindedOn;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stop);   // let the app finish starting

        while (!stop.IsCancellationRequested)
        {
            var today = Clock.Today;
            try
            {
                if (_billedOn != today)
                {
                    await ForEachCentre(async sp => await sp.GetRequiredService<BillingService>().RaiseAsync(null, stop), "billing", stop);
                    _billedOn = today;
                }

                if (_remindedOn != today && Clock.IndiaNow.Hour >= ReminderHour)
                {
                    await ForEachCentre(async sp =>
                    {
                        if (await sp.GetRequiredService<PlanService>().IsActiveAsync(stop))
                            await sp.GetRequiredService<ReminderService>().SendAutomaticAsync(stop);
                    }, "reminders", stop);
                    _remindedOn = today;
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Daily jobs failed");
            }

            await Task.Delay(Every, stop);
        }
    }

    private async Task ForEachCentre(Func<IServiceProvider, Task> work, string name, CancellationToken ct)
    {
        List<Guid> tenantIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClassKendraContext>();
            tenantIds = await db.Tenants.IgnoreQueryFilters().Where(t => t.SetupComplete)
                .Select(t => t.Id).ToListAsync(ct);
        }

        foreach (var id in tenantIds)
        {
            try
            {
                await TenantScope.RunAsync(scopes, id, work);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One centre's problem must not stop the others.
                log.LogError(ex, "Daily {Job} failed for tenant {Tenant}", name, id);
            }
        }
    }
}
