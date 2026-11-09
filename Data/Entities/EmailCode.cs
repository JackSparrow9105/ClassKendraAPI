namespace ClassKendraAPI.Data.Entities;

/// <summary>A one-time code sent by email. Only its hash is stored.</summary>
public class EmailCode
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Where the code was sent. For "Verify" this is the new email, saved on the user only once the code is right.</summary>
    public string Email { get; set; } = "";

    /// <summary>"Verify" (adding a backup email) or "Login" (signing in when the phone doesn't work).</summary>
    public string Purpose { get; set; } = "";

    public byte[] CodeHash { get; set; } = [];

    public byte Attempts { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
