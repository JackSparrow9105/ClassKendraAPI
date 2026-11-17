namespace ClassKendraAPI.Data.Entities;

// Our addition to the generated Student (kept in a separate file so "Reverse Engineer" doesn't overwrite it).
public partial class Student
{
    /// <summary>Left, Transferred, PassedOut or Removed. Null while the student is active.</summary>
    public string? LeftReason { get; set; }
    public string? LeftNote { get; set; }

    /// <summary>The student left with fees still owed and the centre wants reminders to keep going.</summary>
    public bool RemindAfterLeaving { get; set; }
}
