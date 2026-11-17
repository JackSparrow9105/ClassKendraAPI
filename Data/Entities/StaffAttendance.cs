namespace ClassKendraAPI.Data.Entities;

// Staff attendance for schools (Database/2026-11-17-staff-attendance.sql). Written by hand, not reverse engineered.

public partial class User
{
    /// <summary>A different timing for this person; null = the school's timing.</summary>
    public TimeOnly? StaffStartTime { get; set; }
    public TimeOnly? StaffEndTime { get; set; }
}

public class StaffAttendanceSetting
{
    public Guid TenantId { get; set; }
    public TimeOnly StartTime { get; set; } = new(7, 30);
    public TimeOnly EndTime { get; set; } = new(13, 30);
    public int GraceMinutes { get; set; } = 10;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int RadiusMeters { get; set; } = 200;
    /// <summary>Days off, "0" = Sunday … "6" = Saturday, comma separated.</summary>
    public string WeeklyOff { get; set; } = "0";
    public bool NotifyLeaveRequests { get; set; } = true;
    public bool NotifyLeaveAnswers { get; set; } = true;
    public bool LeaveWhatsApp { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class StaffHoliday
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
}

public static class StaffDayStatuses
{
    public const string Present = "Present";
    public const string Late = "Late";
    public const string Absent = "Absent";
    public const string Leave = "Leave";
}

public class StaffAttendanceRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly Date { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string Status { get; set; } = StaffDayStatuses.Present;
    public int LateMinutes { get; set; }
    public int? DistanceMeters { get; set; }
    public Guid? MarkedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public virtual User User { get; set; } = null!;
}

public static class LeaveStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public class StaffLeave
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public int Days { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = LeaveStatuses.Pending;
    public string? RejectReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public virtual User User { get; set; } = null!;
}

public class PushSubscriptionRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
