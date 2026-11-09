namespace ClassKendraAPI.Data.Entities;

/// <summary>
/// One uploaded logo. A new upload adds a new row; rows are never changed or deleted,
/// so a receipt keeps pointing at the logo it was printed with.
/// </summary>
public class CentreLogo
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string ContentType { get; set; } = "";

    public byte[] Data { get; set; } = [];

    public DateTime CreatedAt { get; set; }
}
