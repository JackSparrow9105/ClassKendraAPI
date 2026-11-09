using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for the backup email columns and the EmailCodes table (Database/2026-11-09-backup-email.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<EmailCode> EmailCodes { get; set; }

    private static void ConfigureBackupEmail(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(e => e.Email).HasMaxLength(254);
            entity.Property(e => e.EmailVerifiedAt).HasPrecision(0);
            entity.HasIndex(e => e.Email, "UX_Users_Email").IsUnique().HasFilter("([Email] IS NOT NULL)");
        });

        modelBuilder.Entity<EmailCode>(entity =>
        {
            entity.ToTable("EmailCodes");
            entity.HasIndex(e => new { e.UserId, e.Purpose, e.CreatedAt }, "IX_EmailCodes_User").IsDescending(false, false, true);

            entity.Property(e => e.Email).IsRequired().HasMaxLength(254);
            entity.Property(e => e.Purpose).IsRequired().HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.CodeHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Attempts).HasDefaultValue((byte)0, "DF_EmailCodes_Attempts");
            entity.Property(e => e.ExpiresAt).HasPrecision(0);
            entity.Property(e => e.ConsumedAt).HasPrecision(0);
            entity.Property(e => e.CreatedAt).HasPrecision(0).HasDefaultValueSql("(sysutcdatetime())", "DF_EmailCodes_CreatedAt");

            entity.HasOne(d => d.User).WithMany(p => p.EmailCodes)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_EmailCodes_User");
        });
    }
}
