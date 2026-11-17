using System.Text.Json;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Activity;

public static class AuditActions
{
    public const string PaymentCancelled = "PaymentCancelled";
    public const string StudentLeft = "StudentLeft";
    public const string StudentRejoined = "StudentRejoined";
    public const string FeesWaived = "FeesWaived";
    public const string HolidayAdded = "HolidayAdded";
    public const string HolidayRemoved = "HolidayRemoved";
    public const string CheckInRadiusChanged = "CheckInRadiusChanged";
    public const string Refunded = "Refunded";
    public const string ChargeAdded = "ChargeAdded";
    public const string OpeningBalanceSet = "OpeningBalanceSet";
    public const string FeeSettingsChanged = "FeeSettingsChanged";
    public const string BatchChanged = "BatchChanged";
    public const string DataExported = "DataExported";
    public const string PromotionUndone = "PromotionUndone";
    public const string PaymentRestored = "PaymentRestored";

    public static readonly string[] All =
        [PaymentCancelled, StudentLeft, StudentRejoined, FeesWaived, HolidayAdded, HolidayRemoved, CheckInRadiusChanged,
         Refunded, ChargeAdded, OpeningBalanceSet, FeeSettingsChanged, BatchChanged, DataExported, PromotionUndone, PaymentRestored];
}

/// <summary>
/// Writes the activity log: who did what, when and why. The owner reads it under Settings › Activity log.
/// Nothing in the app edits or deletes these rows.
/// </summary>
public class AuditService(ClassKendraContext db, CurrentUser current)
{
    private string? _name;

    /// <summary>The signed-in person's name ("System" for background jobs).</summary>
    public async Task<string> UserNameAsync(CancellationToken ct)
    {
        if (_name is not null) return _name;
        if (current.IsSystem || !current.IsAuthenticated) return _name = "System";
        var name = await db.Users.AsNoTracking().Where(u => u.Id == current.UserId)
            .Select(u => u.Name).FirstOrDefaultAsync(ct);
        return _name = string.IsNullOrWhiteSpace(name) ? current.Role : name;
    }

    /// <summary>
    /// Adds one line to the log. It is saved by the caller's next SaveChanges, so the line and the change
    /// it describes are written together (or not at all).
    /// </summary>
    public async Task AddAsync(string action, string entityType, Guid? entityId, string summary,
                               string? reason, object? data, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            At = DateTime.UtcNow,
            UserId = current.IsSystem || !current.IsAuthenticated ? null : current.UserId,
            UserName = Cut(await UserNameAsync(ct), 100),
            UserRole = current.Role,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = Cut(summary, 300),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : Cut(reason.Trim(), 300),
            Data = data is null ? null : JsonSerializer.Serialize(data)
        });
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
