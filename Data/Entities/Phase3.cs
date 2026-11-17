namespace ClassKendraAPI.Data.Entities;

// Our additions to generated entities (Database/2026-11-20-undo-and-offline.sql).
public partial class PromotionRun
{
    /// <summary>Set when the promotion was undone (UTC). An undone promotion can't be undone again.</summary>
    public DateTime? UndoneAt { get; set; }
    public string? UndoneByName { get; set; }
}
