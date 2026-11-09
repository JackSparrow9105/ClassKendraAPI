namespace ClassKendraAPI.Data.Entities;

// Our addition to the generated Payment (kept in a separate file so "Reverse Engineer" doesn't overwrite it).
public partial class Payment
{
    /// <summary>
    /// Everything printed on the receipt, frozen as JSON when the payment was recorded
    /// (Features/Fees/ReceiptSnapshot.cs). Null only for payments made before this was added,
    /// until their receipt is first opened.
    /// </summary>
    public string? ReceiptSnapshot { get; set; }
}
