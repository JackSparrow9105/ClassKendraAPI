using ClassKendraAPI.Common;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>
/// Our additions to the generated context (survives "Reverse Engineer" refreshes).
/// Every query automatically sees only the logged-in user's centre.
/// </summary>
public partial class ClassKendraContext
{
	private readonly CurrentUser? _currentUser;

	public ClassKendraContext(DbContextOptions<ClassKendraContext> options, CurrentUser currentUser)
		: this(options)
	{
		_currentUser = currentUser;
	}

	/// <summary>EF reads this fresh on every query.</summary>
	private Guid? CurrentTenantId => _currentUser?.TenantId;

	partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<Tenant>().HasQueryFilter(e => e.Id == CurrentTenantId);
		modelBuilder.Entity<User>().HasQueryFilter(e => e.TenantId != null && e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Subscription>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Batch>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Student>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<FeeSetting>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<FeeHead>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<StudentConcession>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<FeeDue>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<ReceiptSequence>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Payment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<PaymentOrder>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<AttendanceSheet>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Message>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<PromotionRun>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

		ConfigurePins(modelBuilder);   // app PIN columns + UserDevices (ClassKendraContext.Pin.cs)
	}
}