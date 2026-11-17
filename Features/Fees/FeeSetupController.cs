using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

// ---- Shapes that match models.ts (FeeSettings, FeeHead, FeeSetupRequest) ----
/// <param name="LateFeeType">None, Once (a fixed fine) or PerDay.</param>
/// <param name="LateFeeAmount">₹ once, or ₹ per day.</param>
/// <param name="LateFeeAfterDays">Days after the due date before it starts (0 = the day after the due date).</param>
/// <param name="LateFeeMax">PerDay only: the most per fee. 0 = no limit.</param>
/// <param name="SiblingDiscountType">None, Percent or Amount — for the 2nd and later child of a family.</param>
/// <param name="SiblingDiscountHeadId">Null = every fee; otherwise only that fee.</param>
public record FeeSettingsDto(int DueDay, int SessionStartMonth, bool RemindBefore, bool RemindOnDue, bool RemindAfter,
    string? LateFeeType = null, int LateFeeAmount = 0, int LateFeeAfterDays = 0, int LateFeeMax = 0,
    string? SiblingDiscountType = null, int SiblingDiscountValue = 0, string? SiblingDiscountHeadId = null);
/// <param name="AdmissionFee">The admission fee: one-time, charged only to new admissions (at most one).</param>
public record FeeHeadDto(string Id, string Name, string Frequency, bool Optional, Dictionary<string, int> Amounts, bool AdmissionFee = false);
public record FeeSetupDto(FeeSettingsDto? Settings, List<FeeHeadDto> Heads);
public record FeeSetupRequest(FeeSettingsDto Settings, List<FeeHeadDto> Heads);

[ApiController]
[Route("api/v1/fees/setup")]
[Authorize(Roles = "Owner,Staff")]
public class FeeSetupController(ClassKendraContext db, CurrentUser current, BillingService billing, LateFeeService lateFees,
    ClassKendraAPI.Features.Activity.AuditService audit) : ControllerBase
{
    private static readonly string[] Frequencies = ["Monthly", "Quarterly", "HalfYearly", "Yearly", "OneTime"];

    /// <summary>GET /fees/setup -> { settings, heads }</summary>
    [HttpGet]
    public async Task<ActionResult<FeeSetupDto>> Get(CancellationToken ct) => await Load(ct);

    /// <summary>
    /// PUT /fees/setup { settings, heads } — the full list. New heads have id "".
    /// Heads left out are deleted and their unpaid bills cancelled.
    /// Changed amounts apply to bills raised from now on.
    /// </summary>
    [HttpPut]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<FeeSetupDto>> Save(FeeSetupRequest req, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });
        if (Validate(req) is { } error)
            return BadRequest(new { message = error });

        var batchIds = req.Heads.SelectMany(h => h.Amounts.Keys).Distinct().Select(Guid.Parse).ToList();
        if (batchIds.Count > 0 && await db.Batches.CountAsync(b => batchIds.Contains(b.Id), ct) != batchIds.Count)
            return BadRequest(new { message = "One of the batches was removed. Reload and try again." });

        var existing = await db.FeeHeads
            .Include(h => h.FeeHeadAmounts)
            .Include(h => h.Students)
            .Where(h => !h.IsDeleted && h.Kind == null)   // hidden types (late fee, previous dues…) are never in this list
            .ToListAsync(ct);

        Guid? siblingHead = null;
        if (!string.IsNullOrEmpty(req.Settings.SiblingDiscountHeadId))
        {
            if (!Guid.TryParse(req.Settings.SiblingDiscountHeadId, out var sh) || existing.All(e => e.Id != sh))
                return BadRequest(new { message = "The fee chosen for the sibling discount no longer exists." });
            siblingHead = sh;
        }

        var incomingIds = req.Heads.Where(h => h.Id != "").Select(h => Guid.TryParse(h.Id, out var g) ? g : Guid.Empty).ToHashSet();
        if (incomingIds.Any(id => existing.All(e => e.Id != id)))
            return BadRequest(new { message = "A fee you edited was removed meanwhile. Reload and try again." });

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Settings
        var settings = await db.FeeSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new FeeSetting { TenantId = tenantId };
            db.FeeSettings.Add(settings);
        }
        settings.DueDay = (byte)req.Settings.DueDay;
        settings.SessionStartMonth = (byte)req.Settings.SessionStartMonth;
        settings.RemindBefore = req.Settings.RemindBefore;
        settings.RemindOnDue = req.Settings.RemindOnDue;
        settings.RemindAfter = req.Settings.RemindAfter;

        var before = (settings.LateFeeType, settings.LateFeeAmount, settings.LateFeeAfterDays, settings.LateFeeMax,
                      settings.SiblingDiscountType, settings.SiblingDiscountValue, settings.SiblingDiscountHeadId);
        settings.LateFeeType = req.Settings.LateFeeType ?? LateFeeTypes.None;
        settings.LateFeeAmount = settings.LateFeeType == LateFeeTypes.None ? 0 : req.Settings.LateFeeAmount;
        settings.LateFeeAfterDays = req.Settings.LateFeeAfterDays;
        settings.LateFeeMax = settings.LateFeeType == LateFeeTypes.PerDay ? req.Settings.LateFeeMax : 0;
        settings.SiblingDiscountType = req.Settings.SiblingDiscountType ?? "None";
        settings.SiblingDiscountValue = settings.SiblingDiscountType == "None" ? 0 : req.Settings.SiblingDiscountValue;
        settings.SiblingDiscountHeadId = settings.SiblingDiscountType == "None" ? null : siblingHead;
        var after = (settings.LateFeeType, settings.LateFeeAmount, settings.LateFeeAfterDays, settings.LateFeeMax,
                     settings.SiblingDiscountType, settings.SiblingDiscountValue, settings.SiblingDiscountHeadId);
        var siblingChanged = before.SiblingDiscountType != after.SiblingDiscountType
                             || before.SiblingDiscountValue != after.SiblingDiscountValue
                             || before.SiblingDiscountHeadId != after.SiblingDiscountHeadId;
        if (before != after)
            await audit.AddAsync(ClassKendraAPI.Features.Activity.AuditActions.FeeSettingsChanged, "Settings", null,
                $"Late fee: {LateText(after.LateFeeType, after.LateFeeAmount, after.LateFeeAfterDays, after.LateFeeMax)} · " +
                $"Sibling discount: {SiblingText(after.SiblingDiscountType, after.SiblingDiscountValue)}",
                null, null, ct);

        // 2. Removed heads first (frees their names for reuse), and cancel their unpaid bills.
        var removed = existing.Where(e => !incomingIds.Contains(e.Id)).ToList();
        foreach (var head in removed)
        {
            head.IsDeleted = true;          // soft delete: paid bills still point here
            head.Students.Clear();
        }
        await db.SaveChangesAsync(ct);

        if (removed.Count > 0)
        {
            var removedIds = removed.Select(r => r.Id).ToList();
            await db.FeeDues
                .Where(d => removedIds.Contains(d.FeeHeadId) && d.PaidAmount == 0 && d.Status != "Cancelled")
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, "Cancelled")
                    .SetProperty(d => d.CancelledAt, DateTime.UtcNow), ct);
        }

        // 3. New and edited heads.
        foreach (var dto in req.Heads)
        {
            FeeHead head;
            if (dto.Id == "")
            {
                head = new FeeHead { TenantId = tenantId, CreatedAt = DateTime.UtcNow };
                db.FeeHeads.Add(head);
            }
            else
            {
                head = existing.First(e => e.Id == Guid.Parse(dto.Id));
            }

            head.Name = dto.Name.Trim();
            head.IsAdmissionFee = dto.AdmissionFee;
            head.Frequency = dto.AdmissionFee ? "OneTime" : dto.Frequency;   // admission fee: once, everyone admitted pays
            head.IsOptional = !dto.AdmissionFee && dto.Optional;
            if (!head.IsOptional) head.Students.Clear();   // everyone pays now; the opt-in list isn't needed

            // Amounts per batch: update in place, add new, remove dropped or zero.
            var wanted = dto.Amounts.Where(a => a.Value > 0).ToDictionary(a => Guid.Parse(a.Key), a => a.Value);
            foreach (var row in head.FeeHeadAmounts.ToList())
            {
                if (wanted.TryGetValue(row.BatchId, out var amount)) row.Amount = amount;
                else head.FeeHeadAmounts.Remove(row);
            }
            foreach (var (batchId, amount) in wanted)
                if (head.FeeHeadAmounts.All(r => r.BatchId != batchId))
                    head.FeeHeadAmounts.Add(new FeeHeadAmount { BatchId = batchId, Amount = amount });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Raise this month's bills with the new setup (existing bills keep their amounts).
        db.ChangeTracker.Clear();
        // A new sibling discount (or none any more) applies to unpaid fees straight away, like a concession.
        if (siblingChanged) await billing.RepriceManyAsync(null, ct);
        await billing.RaiseAsync(null, ct);
        await lateFees.RaiseAsync(ct);
        return await Load(ct);
    }

    // ---------- helpers ----------

    private static string? Validate(FeeSetupRequest r)
    {
        if (r.Settings.DueDay is < 1 or > 28) return "Due day must be between 1 and 28.";
        if (r.Settings.SessionStartMonth is < 1 or > 12) return "Choose the month your session starts.";

        foreach (var h in r.Heads)
        {
            if (string.IsNullOrWhiteSpace(h.Name)) return "Every fee needs a name.";
            if (!Frequencies.Contains(h.Frequency)) return $"Choose how often \"{h.Name}\" is charged.";
            if (h.Amounts.Keys.Any(k => !Guid.TryParse(k, out _))) return "One of the batches was removed. Reload and try again.";
            if (h.Amounts.Values.Any(v => v < 0)) return $"Amounts for \"{h.Name}\" can't be negative.";
        }

        if (r.Heads.Count(h => h.AdmissionFee) > 1) return "There can be only one admission fee.";

        var s = r.Settings;
        if (!LateFeeTypes.All.Contains(s.LateFeeType ?? LateFeeTypes.None)) return "Choose a late fee type.";
        if (s.LateFeeType is LateFeeTypes.Once or LateFeeTypes.PerDay)
        {
            if (s.LateFeeAmount is <= 0 or > 100_000) return "Enter the late fee amount.";
            if (s.LateFeeAfterDays is < 0 or > 90) return "Start the late fee between 0 and 90 days after the due date.";
            if (s.LateFeeType == LateFeeTypes.PerDay && s.LateFeeMax is < 0 or > 1_000_000) return "Enter a sensible maximum late fee.";
            if (s.LateFeeType == LateFeeTypes.PerDay && s.LateFeeMax > 0 && s.LateFeeMax < s.LateFeeAmount)
                return "The maximum late fee can't be less than one day's late fee.";
        }
        if ((s.SiblingDiscountType ?? "None") is not ("None" or "Percent" or "Amount")) return "Choose a sibling discount type.";
        if (s.SiblingDiscountType == "Percent" && s.SiblingDiscountValue is <= 0 or > 100) return "Enter a sibling discount between 1 and 100%.";
        if (s.SiblingDiscountType == "Amount" && s.SiblingDiscountValue is <= 0 or > 1_000_000) return "Enter the sibling discount in ₹.";

        var duplicate = r.Heads.GroupBy(h => h.Name.Trim().ToLowerInvariant()).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"There are two fees called \"{duplicate.First().Name.Trim()}\".";
    }

    private static string LateText(string type, int amount, int afterDays, int max) => type switch
    {
        LateFeeTypes.Once => $"{Money.Inr(amount)} once, {afterDays} days after the due date",
        LateFeeTypes.PerDay => $"{Money.Inr(amount)} a day from {afterDays} days after the due date" + (max > 0 ? $", up to {Money.Inr(max)}" : ""),
        _ => "none"
    };

    private static string SiblingText(string type, int value) => type switch
    {
        "Percent" => $"{value}%",
        "Amount" => Money.Inr(value),
        _ => "none"
    };

    private async Task<FeeSetupDto> Load(CancellationToken ct)
    {
        var s = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var heads = await db.FeeHeads.AsNoTracking()
            .Include(h => h.FeeHeadAmounts)
            .Where(h => !h.IsDeleted && h.Kind == null)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        return new FeeSetupDto(
            s is null ? null : new FeeSettingsDto(s.DueDay, s.SessionStartMonth, s.RemindBefore, s.RemindOnDue, s.RemindAfter,
                s.LateFeeType, s.LateFeeAmount, s.LateFeeAfterDays, s.LateFeeMax,
                s.SiblingDiscountType, s.SiblingDiscountValue, s.SiblingDiscountHeadId?.ToString()),
            heads.Select(h => new FeeHeadDto(
                h.Id.ToString(), h.Name, h.Frequency, h.IsOptional,
                h.FeeHeadAmounts.ToDictionary(a => a.BatchId.ToString(), a => a.Amount), h.IsAdmissionFee)).ToList());
    }
}