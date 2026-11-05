namespace ClassKendraAPI.Common;

/// <summary>Who is calling the API. Filled once per request by CurrentUserMiddleware.</summary>
public class CurrentUser
{
    public bool IsAuthenticated { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? TenantId { get; private set; }
    public string Role { get; private set; } = "";

    /// <summary>True for background jobs and public pages working on behalf of one centre.</summary>
    public bool IsSystem { get; private set; }

    public void Set(Guid userId, Guid? tenantId, string role)
    {
        IsAuthenticated = true;
        UserId = userId;
        TenantId = tenantId;
        Role = role;
    }

    /// <summary>
    /// Background jobs, the parent pay page and webhooks have no signed-in user.
    /// This switches the tenant filter to one centre so the normal services can be reused safely.
    /// Only call it on a fresh scope (one centre per scope).
    /// </summary>
    public void ActAsTenant(Guid tenantId)
    {
        IsSystem = true;
        TenantId = tenantId;
        Role = "System";
    }
}
