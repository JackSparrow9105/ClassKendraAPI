using System.Security.Cryptography;
using System.Text;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

/// <summary>
/// Bills ClassKendra makes itself, besides the centre's own fees. Each kind has one hidden fee type per centre
/// (FeeHeads.Kind) so the bill can point at it; the hidden types never appear in fee setup or the billing run.
/// </summary>
public static class BillKinds
{
    /// <summary>"Previous dues": what a student owed before the centre started using ClassKendra. Period "OB".</summary>
    public const string OpeningBalance = "OpeningBalance";
    /// <summary>A late fee on one overdue bill (ParentDueId).</summary>
    public const string LateFee = "LateFee";
    /// <summary>An extra charge added to one student (lab breakage, lost ID card…).</summary>
    public const string Charge = "Charge";

    public static readonly string[] All = [OpeningBalance, LateFee, Charge];

    public const string OpeningPeriod = "OB";

    /// <summary>Hidden fee type names. Unique per centre, and unlike anything a centre would type.</summary>
    private static string HeadName(string kind) => kind switch
    {
        OpeningBalance => "~ClassKendra · Previous dues",
        LateFee => "~ClassKendra · Late fee",
        _ => "~ClassKendra · Other charges"
    };

    /// <summary>The centre's hidden fee type for this kind, made the first time it is needed.</summary>
    public static async Task<FeeHead> HeadAsync(ClassKendraContext db, Guid tenantId, string kind, CancellationToken ct)
    {
        var head = await db.FeeHeads.FirstOrDefaultAsync(h => h.Kind == kind && !h.IsDeleted, ct);
        if (head is not null) return head;
        head = new FeeHead
        {
            TenantId = tenantId,
            Name = HeadName(kind),
            Frequency = "OneTime",
            IsOptional = false,
            IsAdmissionFee = false,
            Kind = kind,
            CreatedAt = DateTime.UtcNow
        };
        db.FeeHeads.Add(head);
        await db.SaveChangesAsync(ct);
        return head;
    }

    /// <summary>
    /// A period key (FeeDues.Period is 10 letters) that is unique per student: prefix + 9 hex letters of a hash.
    /// Late fees use the bill they belong to, so the same overdue bill always gets the same late-fee period.
    /// </summary>
    public static string Period(char prefix, string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return prefix + Convert.ToHexString(hash)[..9];
    }

    public static string NewChargePeriod() => Period('C', Guid.NewGuid().ToString());

    /// <summary>Fits text into a column, cutting it with "…" when too long.</summary>
    public static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
