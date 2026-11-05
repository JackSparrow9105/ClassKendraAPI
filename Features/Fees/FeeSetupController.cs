using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

// ---- Shapes that match models.ts (FeeSettings, FeeHead, FeeSetupRequest) ----
public record FeeSettingsDto(int DueDay, int SessionStartMonth, bool RemindBefore, bool RemindOnDue, bool RemindAfter);
public record FeeHeadDto(string Id, string Name, string Frequency, bool Optional, Dictionary<string, int> Amounts);
public record FeeSetupDto(FeeSettingsDto? Settings, List<FeeHeadDto> Heads);
public record FeeSetupRequest(FeeSettingsDto Settings, List<FeeHeadDto> Heads);

[ApiController]
[Route("api/v1/fees/setup")]
[Authorize(Roles = "Owner,Staff")]
public class FeeSetupController(ClassKendraContext db, CurrentUser current, BillingService billing) : ControllerBase
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
            .Where(h => !h.IsDeleted)
            .ToListAsync(ct);

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
            head.Frequency = dto.Frequency;
            head.IsOptional = dto.Optional;
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
        await billing.RaiseAsync(null, ct);
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

        var duplicate = r.Heads.GroupBy(h => h.Name.Trim().ToLowerInvariant()).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"There are two fees called \"{duplicate.First().Name.Trim()}\".";
    }

    private async Task<FeeSetupDto> Load(CancellationToken ct)
    {
        var s = await db.FeeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var heads = await db.FeeHeads.AsNoTracking()
            .Include(h => h.FeeHeadAmounts)
            .Where(h => !h.IsDeleted)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        return new FeeSetupDto(
            s is null ? null : new FeeSettingsDto(s.DueDay, s.SessionStartMonth, s.RemindBefore, s.RemindOnDue, s.RemindAfter),
            heads.Select(h => new FeeHeadDto(
                h.Id.ToString(), h.Name, h.Frequency, h.IsOptional,
                h.FeeHeadAmounts.ToDictionary(a => a.BatchId.ToString(), a => a.Amount))).ToList());
    }
}