namespace ClassKendraAPI.Data.Entities;

/// <summary>One line in the activity log: who did what, when and why (Database/2026-11-18-leaving-holidays-activity-log.sql).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>UTC.</summary>
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>Null = done by the system (background job).</summary>
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string UserRole { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    /// <summary>One line a person can read, e.g. "Cancelled receipt R-2026-0041 (₹2,500) of Aarav Sharma".</summary>
    public string Summary { get; set; } = "";
    /// <summary>What the person typed as the reason.</summary>
    public string? Reason { get; set; }
    /// <summary>JSON with the details (amounts, bills) for support.</summary>
    public string? Data { get; set; }
}
