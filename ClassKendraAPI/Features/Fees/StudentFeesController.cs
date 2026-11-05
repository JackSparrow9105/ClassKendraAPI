using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Students;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Fees;

// Matches StudentFeeHistory in models.ts
public record StudentFeeHistory(
    StudentDto Student, string BatchName, List<FeeDueView> Dues, List<PaymentView> Payments,
    int Balance, int PaidThisSession, string PayLink);

[ApiController]
[Route("api/v1/students/{id:guid}/fees")]
[Authorize(Roles = "Owner,Staff")]
public class StudentFeesController(ClassKendraContext db, FeeReadService views) : ControllerBase
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

        var payments = await db.Payments.AsNoTracking()
            .Include(p => p.PaymentAllocations).ThenInclude(a => a.FeeDue)
            .Where(p => p.StudentId == id)
            .ToListAsync(ct);

        var paymentViews = (await views.PaymentViews(payments, ct))
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.ReceiptNo)
            .ToList();

        var paidThisSession = payments
            .Where(p => FeeViews.SessionYear(p.PaidOn.ToString("yyyy-MM"), sessionStart) == thisSession)
            .Sum(p => p.Amount);

        return new StudentFeeHistory(
            StudentsController.ToDto(student), student.Batch.Name, dueViews, paymentViews,
            dueViews.Sum(d => d.Balance), paidThisSession, views.PayLink(student.PayToken));
    }
}