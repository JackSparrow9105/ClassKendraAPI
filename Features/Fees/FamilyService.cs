using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

/// <summary>
/// A family = students of this centre with the same parent phone number.
/// Used for one WhatsApp reminder per family, paying for several children at once, the parent pay page
/// showing every child, and the sibling discount.
///
/// Sibling discount: the child who joined first (then the one added first) pays full fees; every other
/// child still studying here gets the centre's sibling discount. A student who already has a manual
/// "Sibling" discount doesn't get the automatic one as well.
/// </summary>
public class FamilyService(ClassKendraContext db)
{
    public const string SiblingReason = "Sibling";

    /// <summary>Students who get the automatic sibling discount, among the families of these phone numbers.</summary>
    public async Task<HashSet<Guid>> SiblingsWithDiscountAsync(IReadOnlyCollection<string> phones, CancellationToken ct)
    {
        if (phones.Count == 0) return [];
        var rows = await db.Students.AsNoTracking()
            .Where(s => s.IsActive && phones.Contains(s.ParentPhone))
            .Select(s => new { s.Id, s.ParentPhone, s.JoinedOn, s.CreatedAt })
            .ToListAsync(ct);
        return rows.GroupBy(s => s.ParentPhone)
            .SelectMany(g => g.OrderBy(s => s.JoinedOn).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id).Skip(1))
            .Select(s => s.Id)
            .ToHashSet();
    }

    /// <summary>The centre's sibling discount as a concession, or null when the centre doesn't give one.</summary>
    public static StudentConcession? SiblingConcession(FeeSetting? s)
    {
        if (s is null || s.SiblingDiscountValue <= 0 || s.SiblingDiscountType is not ("Percent" or "Amount")) return null;
        return new StudentConcession
        {
            Reason = SiblingReason,
            Note = "Automatic sibling discount",
            Kind = s.SiblingDiscountType,
            Value = s.SiblingDiscountType == "Percent" ? Math.Min(100, s.SiblingDiscountValue) : s.SiblingDiscountValue,
            FeeHeadId = s.SiblingDiscountHeadId
        };
    }

    /// <summary>
    /// The concessions to price a student's bills with: their own, plus the automatic sibling discount when it applies
    /// (and they don't already have a manual "Sibling" discount).
    /// </summary>
    public static IEnumerable<StudentConcession> WithSibling(IEnumerable<StudentConcession> own, bool isSibling, StudentConcession? sibling)
    {
        var list = own.ToList();
        if (isSibling && sibling is not null && list.All(c => c.Reason != SiblingReason)) list.Add(sibling);
        return list;
    }

    /// <summary>Brothers and sisters of this student (same phone), not the student: still studying, or left but still owing.</summary>
    public async Task<List<Student>> SiblingsOfAsync(Student s, CancellationToken ct) =>
        await db.Students.AsNoTracking()
            .Include(x => x.Batch)
            .Where(x => x.ParentPhone == s.ParentPhone && x.Id != s.Id
                        && (x.IsActive || x.FeeDues.Any(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial)))
            .OrderBy(x => x.JoinedOn).ThenBy(x => x.Name)
            .ToListAsync(ct);

    /// <summary>"Aarav", "Aarav & Diya", "Aarav, Diya & Kabir" — for one family WhatsApp message.</summary>
    public static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " & " + names[^1]
    };
}
