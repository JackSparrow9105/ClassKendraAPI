using System.Text.Json;
using ClassKendraAPI.Data.Entities;

namespace ClassKendraAPI.Features.Fees;

// ---- Everything printed on a receipt, frozen when the payment is recorded (ReceiptSnapshot in models.ts) ----

/// <param name="Layout">Which receipt design to draw it with. Old receipts keep their number, so they always look the same.</param>
/// <param name="Rebuilt">True for payments made before snapshots existed: filled in from the data at the time they were first opened.</param>
public record ReceiptSnapshot(
    int Layout, bool Rebuilt, DateTime CapturedAt,
    ReceiptCentre Centre, ReceiptStudent Student,
    string ReceiptNo, string PaidOn, int Amount, string Mode, string Note, string ReceivedBy,
    List<ReceiptLine> Lines, int Unallocated, int BalanceAfter);

/// <param name="LogoId">The logo row used at the time (null = no logo). Logo rows are never changed.</param>
/// <param name="InstitutionType">"Coaching" or "School" — decides whether the receipt says Batch or Class.</param>
public record ReceiptCentre(
    string Name, string City, string Address, string Phone, string? LogoId, string InstitutionType);

public record ReceiptStudent(string Name, string ParentName, string ParentPhone, string BatchName);

public record ReceiptLine(string HeadName, string PeriodLabel, int Amount);

public static class ReceiptSnapshots
{
    /// <summary>The design new receipts are drawn with. Raise it when the receipt layout changes.</summary>
    public const int CurrentLayout = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ReceiptSnapshot Build(
        Payment p, Tenant centre, ReceiptStudent student, IEnumerable<ReceiptLine> lines,
        int balanceAfter, bool rebuilt)
    {
        var list = lines.ToList();
        return new ReceiptSnapshot(
            CurrentLayout, rebuilt, DateTime.UtcNow,
            new ReceiptCentre(
                centre.Name ?? "", centre.City ?? "", centre.Address ?? "", centre.Phone ?? "",
                centre.LogoId?.ToString(), centre.InstitutionType ?? "Coaching"),
            student,
            p.ReceiptNo, p.PaidOn.ToString("yyyy-MM-dd"), p.Amount, p.Mode, p.Note ?? "", p.ReceivedByName ?? "",
            list,
            Math.Max(0, p.Amount - list.Sum(l => l.Amount)),   // online overpayment that went to no fee
            balanceAfter);
    }

    public static string Serialize(ReceiptSnapshot s) => JsonSerializer.Serialize(s, Json);

    /// <summary>Null if the column is empty or unreadable (the caller then rebuilds it).</summary>
    public static ReceiptSnapshot? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReceiptSnapshot>(json, Json); }
        catch (JsonException) { return null; }
    }
}
