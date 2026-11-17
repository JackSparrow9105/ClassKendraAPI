using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for staff attendance, leave and app notifications (Database/2026-11-17-staff-attendance.sql).</summary>
public partial class ClassKendraContext
{
    public virtual DbSet<StaffAttendanceSetting> StaffAttendanceSettings { get; set; }
    public virtual DbSet<Holiday> Holidays { get; set; }
    public virtual DbSet<StaffAttendanceRecord> StaffAttendance { get; set; }
    public virtual DbSet<StaffLeave> StaffLeaves { get; set; }
    public virtual DbSet<PushSubscriptionRow> PushSubscriptions { get; set; }

    private void ConfigureStaffAttendance(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(e => e.StaffStartTime).HasColumnType("time(0)");
            entity.Property(e => e.StaffEndTime).HasColumnType("time(0)");
        });

        modelBuilder.Entity<StaffAttendanceSetting>(entity =>
        {
            entity.ToTable("StaffAttendanceSettings");
            entity.HasKey(e => e.TenantId);
            entity.Property(e => e.StartTime).HasColumnType("time(0)");
            entity.Property(e => e.EndTime).HasColumnType("time(0)");
            entity.Property(e => e.Latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.Longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.WeeklyOff).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.UpdatedAt).HasColumnType("datetime2(0)");
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Holiday>(entity =>
        {
            entity.ToTable("Holidays");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Name).HasMaxLength(60);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StaffAttendanceRecord>(entity =>
        {
            entity.ToTable("StaffAttendance");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.Date }).IsUnique().HasDatabaseName("UX_StaffAttendance_User_Date");
            entity.Property(e => e.CheckInAt).HasColumnType("datetime2(0)");
            entity.Property(e => e.CheckOutAt).HasColumnType("datetime2(0)");
            entity.Property(e => e.Status).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.UpdatedAt).HasColumnType("datetime2(0)");
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StaffLeave>(entity =>
        {
            entity.ToTable("StaffLeaves");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.Status).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.RejectReason).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).HasColumnType("datetime2(0)");
            entity.Property(e => e.DecidedAt).HasColumnType("datetime2(0)");
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<PushSubscriptionRow>(entity =>
        {
            entity.ToTable("PushSubscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Endpoint).HasMaxLength(600);
            entity.Property(e => e.P256dh).HasMaxLength(200).IsUnicode(false);
            entity.Property(e => e.Auth).HasMaxLength(100).IsUnicode(false);
            entity.Property(e => e.CreatedAt).HasColumnType("datetime2(0)");
            entity.HasIndex(e => e.Endpoint).IsUnique().HasDatabaseName("UX_PushSubscriptions_Endpoint");
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}
