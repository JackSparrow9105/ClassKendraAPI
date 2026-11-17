using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Activity;
using ClassKendraAPI.Features.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

public static class AdjustmentKinds
{
    public const string Waive = "Waive";
    public const string Charge = "Charge";
}

public static class RefundModes
{
    public static readonly string[] All = ["Cash", "UPI", "Cheque", "Bank"];
}

// ---- Shapes that match models.ts ----
public record RefundView(string Id, string RefundNo, int Amount, string Mode, string RefundedOn, string Reason, string By, string CreatedAt);
public record RefundInput(int Amount, string? Mode, string? RefundedOn, string? Reason);
public record WaiveInput(int Amount, string? Reason);
public record ChargeInput(string? Name, int Amount, string? DueDate, string? Reason);
public record AdjustmentView(string Id, string DueId, string Kind, int Amount, string Reason, string By, string CreatedAt);
public record OpeningBalanceInput(int PreviousDues, int AdvancePaid, string? AsOn);

/// <summary>A brother or sister (same parent phone) and what they still owe.</summary>
public record FamilyMember(string StudentId, string Name, string BatchName, bool Active, List<FeeDueView> Dues, int Balance, int Advance);

/// <summary>
/// Money on one student beyond normal payments: refunds, waiving part of a bill, extra charges,
/// the opening balance, and the student's family. Owner and office staff. Every change needs a
/// reason and goes in the activity log.
/// </summary>
[ApiController]
[Route("api/v1/students/{id:guid}")]
[Authorize(Roles = "Owner,Staff")]
public class StudentMoneyController(
    ClassKendraContext db, CurrentUser current, AdvanceService advances, OpeningBalanceService opening,
    FamilyService family, AuditService audit) : ControllerBase
{
    private const int MinReasonLength = 10;

    /// <summary>
    /// POST /students/{id}/refunds { amount, mode, refundedOn, reason } → RefundView
    /// Money given back out of the student's advance. To refund money already on a fee, waive that much of
    /// the fee first: the freed money becomes advance. Refunds can't be undone, so the reason is required.
    /// </summary>
    [HttpPost("refunds")]
    public async Task<ActionResult<RefundView>> Refund(Guid id, RefundInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { message = "Student not found." });

        var mode = input.Mode ?? "";
        if (!RefundModes.All.Contains(mode)) return BadRequest(new { message = "Choose how the money was given back: Cash, UPI, Cheque or Bank." });
        if (input.Amount <= 0) return BadRequest(new { message = "Enter the amount given back." });
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < MinReasonLength) return BadRequest(new { message = $"Write why the money is given back (at least {MinReasonLength} letters)." });
        if (reason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 letters." });
        var on = Clock.Today;
        if (!string.IsNullOrWhiteSpace(input.RefundedOn) && !DateOnly.TryParse(input.RefundedOn, out on))
            return BadRequest(new { message = "Enter a valid date." });
        if (on > Clock.Today) return BadRequest(new { message = "The date can't be in the future." });

        var me = await audit.UserNameAsync(ct);
        Refund refund;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Lock the student's payments so two refunds can't take the same advance.
            await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WITH (UPDLOCK, ROWLOCK) WHERE StudentId = {id}").ToListAsync(ct);
            var advance = await advances.ForAsync(id, ct);
            if (input.Amount > advance)
                return BadRequest(new
                {
                    message = advance == 0
                        ? $"{student.Name} has no advance to give back. To refund money paid for a fee, waive that fee first."
                        : $"{student.Name} has only {Money.Inr(advance)} advance. To refund more, waive part of a paid fee first."
                });

            refund = new Refund
            {
                TenantId = tenantId,
                StudentId = id,
                RefundNo = await NextRefundNo(tenantId, on, ct),
                Amount = input.Amount,
                Mode = mode,
                RefundedOn = on,
                Reason = reason,
                ByUserId = current.UserId,
                ByName = me,
                CreatedAt = DateTime.UtcNow
            };
            db.Refunds.Add(refund);
            await audit.AddAsync(AuditActions.Refunded, "Student", id,
                $"Refunded {Money.Inr(input.Amount)} ({mode}) to {student.Name}'s parent · {refund.RefundNo}", reason,
                new { refund.RefundNo, input.Amount, mode, RefundedOn = on.ToString("yyyy-MM-dd") }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return ToView(refund);
    }

    /// <summary>
    /// POST /students/{id}/dues/{dueId}/waive { amount, reason } → FeeDueView
    /// Lowers one bill by this much (at most the whole bill). If more was already paid than the bill now asks for,
    /// the difference becomes the student's advance (used on the next bills, or refunded).
    /// A bill waived to ₹0 is closed; a late fee waived completely is never charged again.
    /// </summary>
    [HttpPost("dues/{dueId:guid}/waive")]
    public async Task<ActionResult<FeeDueView>> Waive(Guid id, Guid dueId, WaiveInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < MinReasonLength) return BadRequest(new { message = $"Write why this is being waived (at least {MinReasonLength} letters)." });
        if (reason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 letters." });
        if (input.Amount <= 0) return BadRequest(new { message = "Enter how much to waive." });

        var me = await audit.UserNameAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var bill = await db.FeeDues
                .FromSqlInterpolated($"SELECT * FROM FeeDues WITH (UPDLOCK, ROWLOCK) WHERE Id = {dueId}")
                .Include(d => d.Student)
                .FirstOrDefaultAsync(d => d.StudentId == id, ct);
            if (bill is null) return NotFound(new { message = "Fee not found." });
            if (bill.Status == DueStatus.Cancelled) return BadRequest(new { message = "This fee is already removed." });

            var amount = bill.GrossAmount - bill.Discount;
            if (input.Amount > amount) return BadRequest(new { message = $"You can waive at most {Money.Inr(amount)} of this fee." });

            bill.Discount += input.Amount;
            var newAmount = bill.GrossAmount - bill.Discount;
            var released = bill.PaidAmount > newAmount ? await advances.ReleaseAsync(bill, bill.PaidAmount - newAmount, ct) : 0;

            if (newAmount == 0)
            {
                bill.Status = DueStatus.Cancelled;   // closed; a late fee waived completely stays closed
                bill.CancelledAt = DateTime.UtcNow;
            }
            else bill.Status = DueStatus.For(newAmount, bill.PaidAmount);

            db.FeeAdjustments.Add(new FeeAdjustment
            {
                TenantId = tenantId, StudentId = id, FeeDueId = bill.Id, Kind = AdjustmentKinds.Waive,
                Amount = input.Amount, Reason = reason, ByUserId = current.UserId, ByName = me, CreatedAt = DateTime.UtcNow
            });
            var movedText = released > 0 ? $" · {Money.Inr(released)} already paid is now advance" : "";
            await audit.AddAsync(AuditActions.FeesWaived, "Student", id,
                $"Waived {Money.Inr(input.Amount)} of {bill.HeadName} ({bill.PeriodLabel}) for {bill.Student.Name}{movedText}",
                reason, new { DueId = bill.Id, input.Amount, Released = released }, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        db.ChangeTracker.Clear();
        await advances.ApplyAsync([id], ct);   // freed money may cover another unpaid bill
        return await DueView(dueId, ct);
    }

    /// <summary>
    /// POST /students/{id}/charges { name, amount, dueDate, reason } → FeeDueView
    /// An extra charge for this student only (lab breakage, lost ID card, trip…). Shown and paid like any fee.
    /// </summary>
    [HttpPost("charges")]
    public async Task<ActionResult<FeeDueView>> Charge(Guid id, ChargeInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { message = "Student not found." });
        if (!student.IsActive) return BadRequest(new { message = $"{student.Name} has left. Bring them back with Rejoin to add a charge." });

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 60) return BadRequest(new { message = "Name the charge (up to 60 letters), e.g. Lab breakage." });
        if (input.Amount is <= 0 or > 1_000_000) return BadRequest(new { message = "Enter the amount of the charge." });
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length > 300) return BadRequest(new { message = "Keep the note under 300 letters." });
        var due = Clock.Today;
        if (!string.IsNullOrWhiteSpace(input.DueDate) && !DateOnly.TryParse(input.DueDate, out due))
            return BadRequest(new { message = "Enter a valid due date." });

        var head = await BillKinds.HeadAsync(db, tenantId, BillKinds.Charge, ct);
        var bill = new FeeDue
        {
            TenantId = tenantId,
            StudentId = id,
            BatchId = student.BatchId,
            FeeHeadId = head.Id,
            Kind = BillKinds.Charge,
            HeadName = name,
            Period = BillKinds.NewChargePeriod(),
            PeriodLabel = BillKinds.Fit(reason.Length > 0 ? reason : due.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), 40),
            Month = Clock.Today.ToString("yyyy-MM"),
            GrossAmount = input.Amount,
            Discount = 0,
            PaidAmount = 0,
            DueDate = due,
            Status = DueStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        db.FeeDues.Add(bill);
        await db.SaveChangesAsync(ct);   // the bill needs its id before the adjustment row points at it

        db.FeeAdjustments.Add(new FeeAdjustment
        {
            TenantId = tenantId, StudentId = id, FeeDueId = bill.Id, Kind = AdjustmentKinds.Charge, Amount = input.Amount,
            Reason = reason.Length > 0 ? reason : name, ByUserId = current.UserId, ByName = await audit.UserNameAsync(ct), CreatedAt = DateTime.UtcNow
        });
        await audit.AddAsync(AuditActions.ChargeAdded, "Student", id,
            $"Added a charge \"{name}\" of {Money.Inr(input.Amount)} for {student.Name}", reason.Length > 0 ? reason : null,
            new { DueId = bill.Id, input.Amount }, ct);
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        await advances.ApplyAsync([id], ct);
        return await DueView(bill.Id, ct);
    }

    /// <summary>GET /students/{id}/opening-balance → OpeningBalanceView</summary>
    [HttpGet("opening-balance")]
    public async Task<ActionResult<OpeningBalanceView>> GetOpening(Guid id, CancellationToken ct) =>
        await db.Students.AnyAsync(s => s.Id == id, ct)
            ? await opening.GetAsync(id, ct)
            : NotFound(new { message = "Student not found." });

    /// <summary>
    /// PUT /students/{id}/opening-balance { previousDues, advancePaid, asOn } → OpeningBalanceView
    /// What the student owed, or had paid ahead, before the centre started using ClassKendra. 0 removes it.
    /// Can be changed until money is paid against it or the advance is used.
    /// </summary>
    [HttpPut("opening-balance")]
    public async Task<ActionResult<OpeningBalanceView>> SetOpening(Guid id, OpeningBalanceInput input, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { message = "Student not found." });
        var asOn = Clock.Today;
        if (!string.IsNullOrWhiteSpace(input.AsOn) && !DateOnly.TryParse(input.AsOn, out asOn))
            return BadRequest(new { message = "Enter a valid date." });

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                await opening.SetAsync(id, input.PreviousDues, input.AdvancePaid, asOn, ct);
            }
            catch (OpeningBalanceException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            await audit.AddAsync(AuditActions.OpeningBalanceSet, "Student", id,
                $"Opening balance for {student.Name}: previous dues {Money.Inr(input.PreviousDues)}, advance {Money.Inr(input.AdvancePaid)}",
                null, new { input.PreviousDues, input.AdvancePaid, AsOn = asOn.ToString("yyyy-MM-dd") }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await opening.GetAsync(id, ct);
    }

    /// <summary>
    /// GET /students/{id}/family → FamilyMember[] — brothers and sisters (same parent phone), not this student:
    /// still studying here, or left but still owing. Each with their unpaid fees and advance.
    /// </summary>
    [HttpGet("family")]
    public async Task<ActionResult<List<FamilyMember>>> Family(Guid id, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { message = "Student not found." });

        var siblings = await family.SiblingsOfAsync(student, ct);
        if (siblings.Count == 0) return new List<FamilyMember>();
        var ids = siblings.Select(s => s.Id).ToList();
        var today = Clock.Today;
        var dues = (await db.FeeDues.AsNoTracking().Include(d => d.Batch)
                .Where(d => ids.Contains(d.StudentId) && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial))
                .ToListAsync(ct))
            .ToLookup(d => d.StudentId);
        var adv = await advances.ForAsync(ids, ct);

        return siblings.Select(s =>
        {
            var views = dues[s.Id].OrderBy(d => d.DueDate).ThenBy(d => d.HeadName)
                .Select(d => FeeViews.ToView(d, s.Name, d.Batch.Name, today)).ToList();
            return new FamilyMember(s.Id.ToString(), s.Name, s.Batch.Name, s.IsActive, views, views.Sum(v => v.Balance), adv.GetValueOrDefault(s.Id));
        }).ToList();
    }

    // ---------- helpers ----------

    /// <summary>"RF-2026-0001" — numbering restarts each session. The lock keeps two refunds from getting one number.</summary>
    private async Task<string> NextRefundNo(Guid tenantId, DateOnly on, CancellationToken ct)
    {
        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var year = FeeViews.SessionYear(on.ToString("yyyy-MM"), sessionStart);
        var prefix = $"RF-{year}-";
        var next = (await db.Database.SqlQuery<int>($"""
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(RefundNo, 9, 10) AS int)), 0) + 1 AS [Value]
              FROM Refunds WITH (UPDLOCK, HOLDLOCK)
             WHERE TenantId = {tenantId} AND RefundNo LIKE {prefix + "%"}
            """).ToListAsync(ct)).Single();
        return $"{prefix}{next:D4}";
    }

    private async Task<FeeDueView> DueView(Guid dueId, CancellationToken ct)
    {
        var d = await db.FeeDues.AsNoTracking().Include(x => x.Student).Include(x => x.Batch).FirstAsync(x => x.Id == dueId, ct);
        return FeeViews.ToView(d, d.Student.Name, d.Batch.Name, Clock.Today);
    }

    internal static RefundView ToView(Refund r) => new(
        r.Id.ToString(), r.RefundNo, r.Amount, r.Mode, r.RefundedOn.ToString("yyyy-MM-dd"), r.Reason, r.ByName,
        DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc).ToString("O"));

    internal static AdjustmentView ToView(FeeAdjustment a) => new(
        a.Id.ToString(), a.FeeDueId.ToString(), a.Kind, a.Amount, a.Reason, a.ByName,
        DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc).ToString("O"));
}
