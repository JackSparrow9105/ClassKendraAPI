namespace ClassKendraAPI.Common;

/// <summary>Runs work for one centre in its own DI scope (used by background jobs).</summary>
public static class TenantScope
{
    public static async Task RunAsync(IServiceScopeFactory scopes, Guid tenantId,
                                      Func<IServiceProvider, Task> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().ActAsTenant(tenantId);
        await work(scope.ServiceProvider);
    }
}
