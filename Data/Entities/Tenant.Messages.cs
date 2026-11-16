namespace ClassKendraAPI.Data.Entities;

// Our additions to the generated Tenant (kept in a separate file so "Reverse Engineer" doesn't overwrite them).
// Columns come from Database/2026-11-16-message-settings.sql. If you re-scaffold and EF adds them to Tenant.cs, delete them here.
public partial class Tenant
{
    /// <summary>Send the parent a WhatsApp receipt for every payment (the payment screen can still override it).</summary>
    public bool SendReceipts { get; set; } = true;

    /// <summary>Absent alerts to parents: "Every" absence, "TwoDays" = only when absent 2 classes in a row, or "Off".</summary>
    public string AbsentAlerts { get; set; } = AbsentAlertModes.Every;
}

public static class AbsentAlertModes
{
    public const string Every = "Every";
    public const string TwoDays = "TwoDays";
    public const string Off = "Off";

    public static bool IsValid(string? mode) => mode is Every or TwoDays or Off;
}
