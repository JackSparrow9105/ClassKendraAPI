using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>
/// Mapping for cancelled payments, the activity log and students leaving
/// (Database/2026-11-18-leaving-holidays-activity-log.sql).
/// </summary>
public partial class ClassKendraContext
{
    /// <summary>Name of the filter that hides cancelled payments. To see them: IgnoreQueryFilters([NotCancelled]).</summary>
    public const string NotCancelled = "NotCancelled";
    /// <summary>Name of the centre filter on Payments (the other entities use one unnamed filter).</summary>
    public const string TenantFilter = "Tenant";

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    private void ConfigureAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.CancelledAt).HasPrecision(0);
            entity.Property(e => e.CancelledByName).HasMaxLength(100);
            entity.Property(e => e.CancelReason).HasMaxLength(300);
        });

        modelBuilder.Entity<Student>(entity =>
        {
            entity.Property(e => e.LeftReason).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.LeftNote).HasMaxLength(200);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.At).HasPrecision(0);
            entity.Property(e => e.UserName).HasMaxLength(100);
            entity.Property(e => e.UserRole).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.Action).HasMaxLength(40).IsUnicode(false);
            entity.Property(e => e.EntityType).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Summary).HasMaxLength(300);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
