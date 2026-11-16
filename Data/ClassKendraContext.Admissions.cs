using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for admission numbers (Database/2026-11-16-admissions.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<AdmissionSequence> AdmissionSequences { get; set; }

    private void ConfigureAdmissions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>(entity =>
        {
            entity.Property(e => e.AdmissionNo).HasMaxLength(20).IsUnicode(false);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.Property(e => e.SendAdmissionMessages);
        });

        modelBuilder.Entity<AdmissionSequence>(entity =>
        {
            entity.ToTable("AdmissionSequences");
            entity.HasKey(e => new { e.TenantId, e.SessionYear });
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
