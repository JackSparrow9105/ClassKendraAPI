using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for centre address/phone/logo and frozen receipts (Database/2026-11-09-receipt-details.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<CentreLogo> CentreLogos { get; set; }

    private void ConfigureReceipts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.Property(e => e.Address).HasMaxLength(300);
            entity.Property(e => e.Phone).HasMaxLength(20).IsUnicode(false);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.ReceiptSnapshot);   // NVARCHAR(MAX)
        });

        modelBuilder.Entity<CentreLogo>(entity =>
        {
            entity.ToTable("CentreLogos");
            entity.HasIndex(e => e.TenantId, "IX_CentreLogos_Tenant");

            entity.Property(e => e.Id).ValueGeneratedNever();   // random Guid from code: the logo address can't be guessed
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.Data).IsRequired();
            entity.Property(e => e.CreatedAt).HasPrecision(0).HasDefaultValueSql("(sysutcdatetime())", "DF_CentreLogos_CreatedAt");

            entity.HasOne<Tenant>().WithMany()
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_CentreLogos_Tenant");

            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
