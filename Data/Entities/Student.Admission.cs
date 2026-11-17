namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated entities (kept in separate files so "Reverse Engineer" doesn't overwrite them).
// Columns come from Database/2026-11-16-admissions.sql and Database/2026-11-17-new-and-existing.sql.
public partial class Student
{
    /// <summary>"A-2026-0045", "1251" or whatever the school typed from its register. Unique per centre.</summary>
    public string? AdmissionNo { get; set; }

    /// <summary>
    /// true = admitted through ClassKendra (gets the admission fee, one-time fees and the WhatsApp confirmation).
    /// false = was already studying here before the centre started using ClassKendra (and every Excel import).
    /// </summary>
    public bool IsNewAdmission { get; set; }
}

public partial class FeeHead
{
    /// <summary>The centre's admission fee: one-time, charged only to new admissions in the month they join.</summary>
    public bool IsAdmissionFee { get; set; }
}

public partial class Tenant
{
    /// <summary>"Session" = A-2026-0045 (restarts each session), "Serial" = 1251, 1252… continuing the school's register.</summary>
    public string AdmissionNoStyle { get; set; } = AdmissionNoStyles.Session;
}

public static class AdmissionNoStyles
{
    public const string Session = "Session";
    public const string Serial = "Serial";
}

/// <summary>Last admission number used per centre per session (SessionYear 0 = the "Serial" style).</summary>
public partial class AdmissionSequence
{
    public Guid TenantId { get; set; }
    public short SessionYear { get; set; }
    public int LastNumber { get; set; }
}
