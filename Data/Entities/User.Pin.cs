namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated User (kept in a separate file so "Reverse Engineer" doesn't overwrite them).
// If you re-scaffold and EF adds these columns to User.cs too, delete them here.
public partial class User
{
    /// <summary>Salted PBKDF2 hash of the 4-digit app PIN. Null = no PIN set.</summary>
    public string? PinHash { get; set; }

    /// <summary>Wrong PINs in a row. At 5 the PIN is locked until the user logs in with OTP again.</summary>
    public byte PinFailedAttempts { get; set; }

    public DateTime? PinSetAt { get; set; }

    public virtual ICollection<UserDevice> Devices { get; set; } = new List<UserDevice>();
}
