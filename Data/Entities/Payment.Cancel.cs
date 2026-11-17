namespace ClassKendraAPI.Data.Entities;

// Our addition to the generated Payment (kept in a separate file so "Reverse Engineer" doesn't overwrite it).
public partial class Payment
{
    /// <summary>
    /// Set when a payment is cancelled (UTC). Payments are never deleted: the row and its receipt number stay,
    /// the money is taken back off the bills, and every normal query skips it (see the "NotCancelled" filter).
    /// </summary>
    public DateTime? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelledByName { get; set; }
    public string? CancelReason { get; set; }
}
