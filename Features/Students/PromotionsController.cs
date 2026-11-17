using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Activity;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

// Match PromotionRunView and UndoPromotionResult in models.ts.
public record PromotionRunView(string Id, string CreatedAt, string RunBy, int Moved, int Left, int KeptBack,
    string? UndoneAt, string? UndoneBy, bool CanUndo, string? UndoEndsOn);
public record UndoSkip(string StudentId, string Name, string Reason);
public record UndoPromotionResult(int Restored, List<UndoSkip> Skipped, int BillsRemoved);

/// <summary>
/// Undo a promotion (a mistake in "Promote to next class" / "Move students") within 7 days. Owner only.
/// Each student goes back to the batch or class they were in, students marked as leaving come back, and the
/// new class's fees raised since the promotion are removed. A student is left as they are (and listed) when:
///  - money was received for them after the promotion, or the new class's fees already have money on them;
///  - they were moved, marked as left or brought back by hand after the promotion.
/// </summary>
[ApiController]
[Route("api/v1/students/promotions")]
[Authorize(Roles = "Owner")]
public class PromotionsController(ClassKendraContext db, BillingService billing, AuditService audit) : ControllerBase
{
    public const int UndoDays = 7;

    /// <summary>GET /students/promotions — promotions of the last 60 days, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PromotionRunView>>> List(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-60);
        var runs = await db.PromotionRuns.AsNoTracking()
            .Where(r => r.CreatedAt >= since)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.CreatedAt, By = r.RunByUser.Name, r.Moved, r.Left, r.KeptBack, r.UndoneAt, r.UndoneByName })
            .ToListAsync(ct);
        return runs.Select(r =>
        {
            var ends = r.CreatedAt.AddDays(UndoDays);
            return new PromotionRunView(r.Id.ToString(), Utc(r.CreatedAt), r.By ?? "", r.Moved, r.Left, r.KeptBack,
                r.UndoneAt is DateTime u ? Utc(u) : null, r.UndoneByName,
                r.UndoneAt is null && DateTime.UtcNow < ends && (r.Moved + r.Left) > 0,
                DateOnly.FromDateTime(ends.AddMinutes(330)).ToString("yyyy-MM-dd"));
        }).ToList();
    }

    /// <summary>POST /students/promotions/{id}/undo → { restored, skipped: [{ studentId, name, reason }], billsRemoved }</summary>
    [HttpPost("{id:guid}/undo")]
    public async Task<ActionResult<UndoPromotionResult>> Undo(Guid id, CancellationToken ct)
    {
        var run = await db.PromotionRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return NotFound(new { message = "Promotion not found." });
        if (run.UndoneAt is not null) return BadRequest(new { message = "This promotion was already undone." });
        if (DateTime.UtcNow > run.CreatedAt.AddDays(UndoDays))
            return BadRequest(new { message = $"A promotion can be undone only within {UndoDays} days." });

        var me = await audit.UserNameAsync(ct);
        var skipped = new List<UndoSkip>();
        var restoredIds = new List<Guid>();
        var phones = new HashSet<string>();
        var billsRemoved = 0;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var moves = await db.StudentBatchHistories
                .Include(h => h.Student)
                .Where(h => h.PromotionRunId == id)
                .ToListAsync(ct);
            var ids = moves.Select(h => h.StudentId).Distinct().ToList();

            // Money received for these students after the promotion (not counting advance from before ClassKendra).
            var paidSince = (await db.Payments.AsNoTracking()
                    .Where(p => ids.Contains(p.StudentId) && p.CreatedAt > run.CreatedAt && p.Mode != PaymentModes.Opening)
                    .Select(p => p.StudentId).Distinct().ToListAsync(ct))
                .ToHashSet();
            // Bills raised since the promotion (the new class's fees, and late fees on them).
            var newBills = (await db.FeeDues
                    .Where(d => ids.Contains(d.StudentId) && d.CreatedAt > run.CreatedAt
                                && (d.Kind == null || d.Kind == BillKinds.LateFee) && d.Status != DueStatus.Cancelled)
                    .ToListAsync(ct))
                .ToLookup(d => d.StudentId);
            // Changed by hand after the promotion (another move, left, rejoined).
            var laterMoves = (await db.StudentBatchHistories.AsNoTracking()
                    .Where(h => ids.Contains(h.StudentId) && h.ChangedAt > run.CreatedAt && h.PromotionRunId != id)
                    .Select(h => h.StudentId).Distinct().ToListAsync(ct))
                .ToHashSet();

            foreach (var h in moves)
            {
                var s = h.Student;
                var wasLeft = h.ToBatchId is null;
                string? why =
                    paidSince.Contains(s.Id) ? "Money was received for this student after the promotion."
                    : newBills[s.Id].Any(b => b.PaidAmount > 0) ? "The new class's fees already have money on them."
                    : laterMoves.Contains(s.Id) || (wasLeft ? s.IsActive : s.BatchId != h.ToBatchId || !s.IsActive)
                        ? "Changed by hand after the promotion."
                    : h.FromBatchId is null ? "The old class is not known."
                    : null;
                if (why is not null)
                {
                    skipped.Add(new UndoSkip(s.Id.ToString(), s.Name, why));
                    continue;
                }

                foreach (var b in newBills[s.Id])
                {
                    b.Status = DueStatus.Cancelled;
                    b.CancelledAt = DateTime.UtcNow;
                    billsRemoved++;
                }
                var now = s.BatchId;
                s.BatchId = h.FromBatchId!.Value;
                if (wasLeft)
                {
                    s.IsActive = true;
                    s.LeftOn = null;
                    s.LeftReason = null;
                    s.LeftNote = null;
                    s.RemindAfterLeaving = false;
                }
                s.UpdatedAt = DateTime.UtcNow;
                db.StudentBatchHistories.Add(new StudentBatchHistory
                {
                    StudentId = s.Id, FromBatchId = wasLeft ? null : now, ToBatchId = h.FromBatchId, ChangedAt = DateTime.UtcNow
                });
                restoredIds.Add(s.Id);
                phones.Add(s.ParentPhone);
            }

            run.UndoneAt = DateTime.UtcNow;
            run.UndoneByName = me;
            var skippedText = skipped.Count > 0 ? $"; {skipped.Count} left as they are" : "";
            await audit.AddAsync(AuditActions.PromotionUndone, "Settings", run.Id,
                $"Undid the promotion of {Ist(run.CreatedAt):dd/MM/yyyy HH:mm}: {restoredIds.Count} students back in their old class{skippedText}",
                null, new { Restored = restoredIds, Skipped = skipped, BillsRemoved = billsRemoved }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Bills of the old class for this month (they were never removed), and sibling discounts for families that changed.
        if (restoredIds.Count > 0)
        {
            db.ChangeTracker.Clear();
            await billing.RaiseAsync(restoredIds, ct);
            await billing.RepriceFamiliesAsync(phones.ToList(), ct);
        }
        return new UndoPromotionResult(restoredIds.Count, skipped, billsRemoved);
    }

    private static DateTime Ist(DateTime utc) => utc.AddMinutes(330);
    private static string Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O");
}
