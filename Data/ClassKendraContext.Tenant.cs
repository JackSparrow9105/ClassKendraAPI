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
		// Payments have two filters: the centre, and "not cancelled". Cancelled payments stay in the table
		// (with their receipt number) but no total, report or balance counts them.
		// To show them: db.Payments.IgnoreQueryFilters([NotCancelled]) — the centre filter stays on.
		modelBuilder.Entity<Payment>()
			.HasQueryFilter(TenantFilter, e => e.TenantId == CurrentTenantId)
			.HasQueryFilter(NotCancelled, e => e.CancelledAt == null);
		modelBuilder.Entity<PaymentOrder>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<AttendanceSheet>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<Message>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
		modelBuilder.Entity<PromotionRun>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

		ConfigurePins(modelBuilder);   // app PIN columns + UserDevices (ClassKendraContext.Pin.cs)
		ConfigureReceipts(modelBuilder);   // centre address/phone/logo + frozen receipts (ClassKendraContext.Receipt.cs)
		ConfigureBackupEmail(modelBuilder);   // backup email + EmailCodes (ClassKendraContext.BackupEmail.cs)
		ConfigureMessageSettings(modelBuilder);   // receipts on/off, absent alert rule (ClassKendraContext.Messages.cs)
		ConfigureAdmissions(modelBuilder);   // admission numbers + confirmations (ClassKendraContext.Admissions.cs)
		ConfigureAudit(modelBuilder);
		ConfigureMoney(modelBuilder);   // advance, refunds, adjustments, opening balance, late fee, siblings (ClassKendraContext.Money.cs)   // cancelled payments, activity log, students leaving (ClassKendraContext.Audit.cs)
		ConfigureStaffAttendance(modelBuilder);   // staff attendance, leave, app notifications (ClassKendraContext.StaffAttendance.cs)
	}
}