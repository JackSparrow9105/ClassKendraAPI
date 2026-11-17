using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for admissions (Database/2026-11-16-admissions.sql, 2026-11-17-new-and-existing.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<AdmissionSequence> AdmissionSequences { get; set; }

    private void ConfigureAdmissions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>(entity =>
        {
            entity.Property(e => e.AdmissionNo).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.IsNewAdmission);
        });

        modelBuilder.Entity<FeeHead>(entity =>
        {
            entity.Property(e => e.IsAdmissionFee);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.Property(e => e.SendAdmissionMessages);
            entity.Property(e => e.AdmissionNoStyle).HasMaxLength(10).IsUnicode(false);
        });

        modelBuilder.Entity<AdmissionSequence>(entity =>
        {
            entity.ToTable("AdmissionSequences");
            entity.HasKey(e => new { e.TenantId, e.SessionYear });
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
