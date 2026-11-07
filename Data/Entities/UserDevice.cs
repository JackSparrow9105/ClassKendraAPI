namespace ClassKendraAPI.Data.Entities;

/// <summary>A phone or browser that logged in with OTP. The app holds the key; we store its SHA-256 hash.</summary>
public class UserDevice
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public byte[] KeyHash { get; set; } = [];

    public string UserAgent { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime LastUsedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public virtual User User { get; set; } = null!;
}
