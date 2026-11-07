using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for the app PIN columns and the UserDevices table (Database/2026-11-07-app-pin.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<UserDevice> UserDevices { get; set; }

    private static void ConfigurePins(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(e => e.PinHash).HasMaxLength(200).IsUnicode(false);
            entity.Property(e => e.PinFailedAttempts).HasDefaultValue((byte)0, "DF_Users_PinFailedAttempts");
            entity.Property(e => e.PinSetAt).HasPrecision(0);
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.ToTable("UserDevices");
            entity.HasIndex(e => e.KeyHash, "UX_UserDevices_KeyHash").IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ExpiresAt }, "IX_UserDevices_User");

            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())", "DF_UserDevices_Id");
            entity.Property(e => e.KeyHash).HasMaxLength(32).IsFixedLength();
            entity.Property(e => e.UserAgent).IsRequired().HasMaxLength(200).HasDefaultValue("", "DF_UserDevices_UserAgent");
            entity.Property(e => e.CreatedAt).HasPrecision(0).HasDefaultValueSql("(sysutcdatetime())", "DF_UserDevices_CreatedAt");
            entity.Property(e => e.LastUsedAt).HasPrecision(0).HasDefaultValueSql("(sysutcdatetime())", "DF_UserDevices_LastUsedAt");
            entity.Property(e => e.ExpiresAt).HasPrecision(0);

            entity.HasOne(d => d.User).WithMany(p => p.Devices)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_UserDevices_User");
        });
    }
}
