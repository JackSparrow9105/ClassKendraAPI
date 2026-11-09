namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated Tenant (kept in a separate file so "Reverse Engineer" doesn't overwrite them).
// Columns come from Database/2026-11-09-receipt-details.sql. If you re-scaffold and EF adds them to Tenant.cs, delete them here.
public partial class Tenant
{
    /// <summary>Printed on receipts. Null = not filled in yet.</summary>
    public string? Address { get; set; }

    /// <summary>Contact number printed on receipts (mobile or landline, as typed).</summary>
    public string? Phone { get; set; }

    /// <summary>The current logo (CentreLogos.Id). Null = no logo.</summary>
    public Guid? LogoId { get; set; }
}
