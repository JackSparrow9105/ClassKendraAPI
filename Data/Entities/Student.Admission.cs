namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated Student (kept in a separate file so "Reverse Engineer" doesn't overwrite them).
// Column comes from Database/2026-11-16-admissions.sql.
public partial class Student
{
    /// <summary>"A-2026-0045": given when the student is added; numbering restarts each session. Null for older students.</summary>
    public string? AdmissionNo { get; set; }
}

/// <summary>Last admission number used per centre per session (like ReceiptSequences).</summary>
public partial class AdmissionSequence
{
    public Guid TenantId { get; set; }
    public short SessionYear { get; set; }
    public int LastNumber { get; set; }
}
