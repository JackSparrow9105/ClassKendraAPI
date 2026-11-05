namespace ClassKendraAPI.Common;

/// <summary>Who is calling the API. Filled once per request by CurrentUserMiddleware.</summary>
public class CurrentUser
{
    public bool IsAuthenticated { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? TenantId { get; private set; }
    public string Role { get; private set; } = "";

    public void Set(Guid userId, Guid? tenantId, string role)
    {
        IsAuthenticated = true;
        UserId = userId;
        TenantId = tenantId;
        Role = role;
    }
}