using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Students;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

// Matches StudentFeeHistory in models.ts
/// <param name="Advance">Money paid that isn't on any fee yet; used automatically on the next fees, or refunded.</param>
/// <param name="Opening">Previous dues / advance from before ClassKendra.</param>
/// <param name="Adjustments">Parts of fees waived and extra charges, newest first.</param>
/// <param name="SiblingCount">Brothers and sisters (same parent phone) still studying here or still owing.</param>
public record StudentFeeHistory(
    StudentDto Student, string BatchName, List<FeeDueView> Dues, List<PaymentView> Payments,
    int Balance, int PaidThisSession, string PayLink,
    int Advance = 0, List<RefundView>? Refunds = null, OpeningBalanceView? Opening = null,
    List<AdjustmentView>? Adjustments = null, int SiblingCount = 0);

[ApiController]
[Route("api/v1/students/{id:guid}/fees")]
[Authorize(Roles = "Owner,Staff")]
public class StudentFeesController(ClassKendraContext db, FeeReadService views,
    ClassKendraAPI.Features.Payments.AdvanceService advances, OpeningBalanceService opening, FamilyService family) : ControllerBase
{
    /// <summary>GET /students/{id}/fees — this session's bills, any older unpaid ones, and receipts.</summary>
    [HttpGet]
    public async Task<ActionResult<StudentFeeHistory>> Get(Guid id, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking()
            .Include(s => s.FeeHeads)
            .Include(s => s.StudentConcessions)
            .Include(s => s.Batch)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { message = "Student not found." });

        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var today = Clock.Today;
        var thisSession = FeeViews.SessionYear(today.ToString("yyyy-MM"), sessionStart);

        var dues = await db.FeeDues.AsNoTracking()
            .Include(d => d.Batch)
            .Where(d => d.StudentId == id && d.Status != "Cancelled")
            .ToListAsync(ct);

        var dueViews = dues
            .Where(d => FeeViews.SessionYear(d.Month, sessionStart) == thisSession || d.Status != "Paid")
            .OrderByDescending(d => d.DueDate).ThenBy(d => d.HeadName)
            .Select(d => FeeViews.ToView(d, student.Name, d.Batch.Name, today))
            .ToList();

        // Cancelled receipts are listed too (marked cancelled) so the history is complete; they count for nothing.
        var payments = await db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled]).AsNoTracking()
            .Include(p => p.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .Where(p => p.StudentId == id)
            .ToListAsync(ct);

        var paymentViews = (await views.PaymentViews(payments, ct))
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.ReceiptNo)
            .ToList();

        var paidThisSession = payments
            .Where(p => p.CancelledAt is null && p.Mode != ClassKendraAPI.Features.Payments.PaymentModes.Opening)
            .Where(p => FeeViews.SessionYear(p.PaidOn.ToString("yyyy-MM"), sessionStart) == thisSession)
            .Sum(p => p.Amount);

        var refunds = await db.Refunds.AsNoTracking().Where(r => r.StudentId == id)
            .OrderByDescending(r => r.RefundedOn).ThenByDescending(r => r.CreatedAt).ToListAsync(ct);
        var adjustments = await db.FeeAdjustments.AsNoTracking().Where(a => a.StudentId == id)
            .OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct);

        return new StudentFeeHistory(
            StudentsController.ToDto(student), student.Batch.Name, dueViews, paymentViews,
            dueViews.Sum(d => d.Balance), paidThisSession, views.PayLink(student.PayToken),
            await advances.ForAsync(id, ct),
            refunds.Select(StudentMoneyController.ToView).ToList(),
            await opening.GetAsync(id, ct),
            adjustments.Select(StudentMoneyController.ToView).ToList(),
            (await family.SiblingsOfAsync(student, ct)).Count);
    }
}
