namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated User (kept in a separate file so "Reverse Engineer" doesn't overwrite them).
// Columns come from Database/2026-11-09-backup-email.sql. If you re-scaffold and EF adds them to User.cs, delete them here.
public partial class User
{
    /// <summary>Verified backup email (lower case). Null = none. Login codes can be sent here if the phone stops working.</summary>
    public string? Email { get; set; }

    public DateTime? EmailVerifiedAt { get; set; }

    public virtual ICollection<EmailCode> EmailCodes { get; set; } = new List<EmailCode>();
}
