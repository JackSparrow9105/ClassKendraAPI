using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>
/// Mapping for advance payments, refunds, bill adjustments, opening balances, late fees and siblings
/// (Database/2026-11-19-advance-refunds-late-fees-siblings.sql).
/// </summary>
public partial class ClassKendraContext
{
    public virtual DbSet<Refund> Refunds { get; set; }
    public virtual DbSet<FeeAdjustment> FeeAdjustments { get; set; }

    private void ConfigureMoney(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeeHead>(entity =>
        {
            entity.Property(e => e.Kind).HasMaxLength(15).IsUnicode(false);
        });

        modelBuilder.Entity<FeeDue>(entity =>
        {
            entity.Property(e => e.Kind).HasMaxLength(15).IsUnicode(false);
            entity.HasIndex(e => e.ParentDueId, "IX_FeeDues_ParentDue").HasFilter("([ParentDueId] IS NOT NULL)");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasIndex(e => e.GroupId, "IX_Payments_Group").HasFilter("([GroupId] IS NOT NULL)");
        });

        modelBuilder.Entity<FeeSetting>(entity =>
        {
            entity.Property(e => e.LateFeeType).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.SiblingDiscountType).HasMaxLength(10).IsUnicode(false);
        });

        modelBuilder.Entity<PromotionRun>(entity =>
        {
            entity.Property(e => e.UndoneAt).HasPrecision(0);
            entity.Property(e => e.UndoneByName).HasMaxLength(100);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("Refunds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.RefundNo).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Mode).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ByName).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasPrecision(0);
            entity.HasIndex(e => new { e.TenantId, e.RefundNo }, "UX_Refunds_Tenant_No").IsUnique();
            entity.HasIndex(e => e.StudentId, "IX_Refunds_Student");
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<FeeAdjustment>(entity =>
        {
            entity.ToTable("FeeAdjustments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Kind).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ByName).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasPrecision(0);
            entity.HasIndex(e => e.FeeDueId, "IX_FeeAdjustments_Due");
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt }, "IX_FeeAdjustments_Tenant");
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
