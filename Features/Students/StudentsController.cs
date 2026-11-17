using System.Security.Cryptography;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Activity;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Students;

// ---- Shapes that match models.ts (Student, Concession, StudentInput) ----
public record ConcessionDto(string Id, string Reason, string Note, string? FeeHeadId, string Kind, int Value);

/// <param name="AdmissionNo">"A-2026-0045", "1251" or the school's own register number.</param>
/// <param name="AdmissionMessageSent">Only on POST /students: was the WhatsApp admission confirmation queued?</param>
/// <param name="IsNewAdmission">true = admitted through ClassKendra; false = was already studying here.</param>
/// <param name="LeftOn">Only for students who left (Active = false): their last day.</param>
public record StudentDto(
    string Id, string Name, string ParentName, string ParentPhone, string BatchId,
    string JoinedOn, bool Active, string[] OptionalHeadIds, ConcessionDto[] Concessions,
    string? AdmissionNo = null, bool? AdmissionMessageSent = null, bool IsNewAdmission = false,
    string? LeftOn = null, string? LeftReason = null, string? LeftNote = null, bool RemindAfterLeaving = false);

/// <summary>One page of students. NewCount = new admissions this session among ALL matching students (for the "New admissions · 12" chip).</summary>
public record StudentPage(List<StudentDto> Items, int Total, int Page, int PageSize, int NewCount);

/// <param name="LeftOn">Last day (yyyy-MM-dd). Empty = today. Not in the future, not before the joining date.</param>
/// <param name="Reason">Left, Transferred, PassedOut or Removed.</param>
/// <param name="Dues">"Keep" (default) = fees already raised are still owed. "Waive" = write off everything unpaid (needs WaiveReason).</param>
/// <param name="KeepReminding">With Dues = Keep: keep sending WhatsApp fee reminders for what is still owed (default true).</param>
public record LeaveInput(string? LeftOn, string? Reason, string? Note, string? Dues, string? WaiveReason, bool? KeepReminding);

/// <summary>What happened to the student's bills when they left.</summary>
public record LeaveResult(StudentDto Student, int BillsCancelled, int StillOwed, int Waived);

/// <param name="BatchId">Empty = the batch or class they were in.</param>
public record RejoinInput(string? BatchId);

/// <param name="SendConfirmation">POST only: false = don't send the WhatsApp admission confirmation for this student.</param>
/// <param name="AdmissionNo">Typed from the register. Empty on POST = made automatically; empty on PUT = unchanged.</param>
/// <param name="IsNewAdmission">POST: default true. false = existing student (no admission fee, one-time fees or WhatsApp).</param>
/// <param name="WaiveAdmissionFee">POST, new admissions only: don't charge this student the admission fee.</param>
/// <param name="ChargeNewBatchThisMonth">PUT, when the batch changes: true = this month's unpaid fees are re-priced at the
/// new batch's amounts. Default false = this month stays at the old batch's fees and the new fees start next month.</param>
public record StudentInput(
    string Name, string? ParentName, string ParentPhone, string BatchId,
    string? JoinedOn, string[]? OptionalHeadIds, bool? SendConfirmation = null,
    string? AdmissionNo = null, bool? IsNewAdmission = null, bool? WaiveAdmissionFee = null,
    bool? ChargeNewBatchThisMonth = null);

public record ConcessionInput(string Reason, string? Note, string? FeeHeadId, string Kind, int Value);

[ApiController]
[Route("api/v1/students")]
[Authorize]
public class StudentsController(ClassKendraContext db, CurrentUser current, BillingService billing, PlanService plans,
                                AdmissionService admissions, AuditService audit) : ControllerBase
{
    private static readonly string[] Reasons = ["Sibling", "Staff child", "Scholarship", "Other"];
    private static readonly string[] LeftReasons = ["Left", "Transferred", "PassedOut", "Removed"];
    private const int MinReasonLength = 10;

    /// <summary>GET /students?batchId=&search= — active students only; teachers see their own classes.</summary>
    [HttpGet]
    public async Task<ActionResult<List<StudentDto>>> List([FromQuery] Guid? batchId, [FromQuery] string? search, CancellationToken ct)
    {
        var q = WithDetails().AsNoTracking().Where(s => s.IsActive);

        if (current.Role == "Teacher")
            q = q.Where(s => s.Batch.Users.Any(u => u.Id == current.UserId));
        if (batchId is Guid b)
            q = q.Where(s => s.BatchId == b);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(s => s.Name.Contains(term) || s.ParentPhone.Contains(term));
        }

        var rows = await q.OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>
    /// GET /students/page?batchId=&search=&kind=&status=&page=&pageSize= — the Students screen, 50 at a time, by name.
    ///   status  Active (default), Left (students who left; office only) or All (both; office only — the search bar)
    ///   kind    new = new admissions of this session · existing = everyone else · empty = all
    ///   search  name, parent's phone or admission number
    /// Teachers see their own classes only.
    /// </summary>
    [HttpGet("page")]
    public async Task<ActionResult<StudentPage>> Page([FromQuery] Guid? batchId, [FromQuery] string? search,
        [FromQuery] string? kind, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var (p, size, skip) = Paging.Clean(page, pageSize);
        var left = string.Equals(status, "Left", StringComparison.OrdinalIgnoreCase);
        // "All" = studying here and left (the search bar). Teachers only ever see students studying here.
        var all = string.Equals(status, "All", StringComparison.OrdinalIgnoreCase) && current.Role != "Teacher";
        if (left && current.Role == "Teacher") return Forbid();

        var q = db.Students.AsNoTracking();
        if (!all) q = q.Where(s => s.IsActive == !left);
        if (current.Role == "Teacher")
            q = q.Where(s => s.Batch.Users.Any(u => u.Id == current.UserId));
        if (batchId is Guid b)
            q = q.Where(s => s.BatchId == b);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(s => s.Name.Contains(term) || s.ParentPhone.Contains(term)
                             || (s.AdmissionNo != null && s.AdmissionNo.Contains(term)));
        }

        // "New" = admitted through ClassKendra since this session started (April unless the centre chose another month).
        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var today = Clock.Today;
        var newSince = new DateOnly(today.Month >= sessionStart ? today.Year : today.Year - 1, sessionStart, 1);

        var newCount = await q.CountAsync(s => s.IsNewAdmission && s.JoinedOn >= newSince, ct);
        if (kind == "new") q = q.Where(s => s.IsNewAdmission && s.JoinedOn >= newSince);
        else if (kind == "existing") q = q.Where(s => !(s.IsNewAdmission && s.JoinedOn >= newSince));

        var total = await q.CountAsync(ct);
        var ordered = left
            ? q.OrderByDescending(s => s.LeftOn).ThenBy(s => s.Name).ThenBy(s => s.Id)   // most recently left first
            : q.OrderByDescending(s => s.IsActive).ThenBy(s => s.Name).ThenBy(s => s.Id);   // (All: students studying here first)
        var rows = await ordered.Skip(skip).Take(size)
            .Include(s => s.FeeHeads).Include(s => s.StudentConcessions)
            .AsSplitQuery()
            .ToListAsync(ct);

        return new StudentPage(rows.Select(ToDto).ToList(), total, p, size, newCount);
    }

    /// <summary>GET /students/{id}</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StudentDto>> Get(Guid id, CancellationToken ct)
    {
        var s = await WithDetails().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || (current.Role == "Teacher" && !await TeacherCanSee(s.BatchId, ct)))
            return NotFound(new { message = "Student not found." });
        return ToDto(s);
    }

    /// <summary>POST /students</summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> Create(StudentInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId)
            return BadRequest(new { message = "Set up your centre first." });

        var (clean, error) = await Validate(input, null, ct);
        if (error is not null) return BadRequest(new { message = error });
        if (await plans.StudentLimitErrorAsync(1, ct) is { } limit) return BadRequest(new { message = limit });

        var s = new Student
        {
            TenantId = tenantId,
            PayToken = NewPayToken(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var (typedNo, noError) = await admissions.CheckTypedAsync(input.AdmissionNo, null, ct);
        if (noError is not null) return BadRequest(new { message = noError });

        Apply(s, clean!);
        s.IsNewAdmission = input.IsNewAdmission ?? true;
        s.AdmissionNo = typedNo;
        foreach (var head in await LoadOptionalHeads(clean!.OptionalHeadIds, ct)) s.FeeHeads.Add(head);
        await admissions.AssignNumbersAsync(tenantId, [s], ct);   // only if none was typed
        if (s.IsNewAdmission && input.WaiveAdmissionFee == true) await admissions.WaiveAdmissionFeeAsync(s, ct);

        db.Students.Add(s);
        await db.SaveChangesAsync(ct);
        await billing.RaiseAsync([s.Id], ct);   // this month's bills (admission fee only for new admissions)
        await billing.RepriceFamiliesAsync([s.ParentPhone], ct);   // a brother or sister may now get the sibling discount

        // New admissions: WhatsApp admission confirmation with the fees due now (the centre can turn these off).
        var sent = s.IsNewAdmission && input.SendConfirmation != false
                   && await admissions.SendConfirmationAsync(s.Id, force: false, ct);
        return ToDto(s) with { AdmissionMessageSent = s.IsNewAdmission ? sent : null };
    }

    /// <summary>POST /students/{id}/admission-message — send the admission confirmation again → { sent }.</summary>
    [HttpPost("{id:guid}/admission-message")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<IActionResult> ResendAdmission(Guid id, CancellationToken ct)
    {
        var exists = await db.Students.AnyAsync(s => s.Id == id && s.AdmissionNo != null && s.IsNewAdmission, ct);
        if (!exists) return NotFound(new { message = "Admission confirmations are only for new admissions." });
        var sent = await admissions.SendConfirmationAsync(id, force: true, ct);
        return sent ? Ok(new { sent })
                    : BadRequest(new { message = "Couldn't send it. This month's WhatsApp messages may be used up." });
    }

    /// <summary>PUT /students/{id} — opting out of an optional fee cancels its unpaid bills.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> Update(Guid id, StudentInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        var (clean, error) = await Validate(input, id, ct);
        if (error is not null) return BadRequest(new { message = error });

        var (typedNo, noError) = await admissions.CheckTypedAsync(input.AdmissionNo, id, ct);
        if (noError is not null) return BadRequest(new { message = noError });

        var droppedHeads = s.FeeHeads.Select(h => h.Id).Except(clean!.OptionalHeadIds).ToList();
        var oldBatch = s.BatchId;
        var oldPhone = s.ParentPhone;
        // Marked as an existing student after all: the unpaid admission fee goes too.
        if (input.IsNewAdmission == false && s.IsNewAdmission && await admissions.AdmissionFeeAsync(ct) is { } admissionFee)
            droppedHeads.Add(admissionFee.Id);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Apply(s, clean);
        if (typedNo is not null) s.AdmissionNo = typedNo;
        if (input.IsNewAdmission is bool isNew) s.IsNewAdmission = isNew;
        s.FeeHeads.Clear();
        foreach (var head in await LoadOptionalHeads(clean.OptionalHeadIds, ct)) s.FeeHeads.Add(head);
        await db.SaveChangesAsync(ct);

        if (droppedHeads.Count > 0) await CancelUnpaidBills(s.Id, droppedHeads, ct);

        var moved = clean.BatchId != oldBatch;
        if (moved)
        {
            var names = await db.Batches.AsNoTracking().Where(b => b.Id == oldBatch || b.Id == clean.BatchId)
                .ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            db.StudentBatchHistories.Add(new StudentBatchHistory
            { StudentId = s.Id, FromBatchId = oldBatch, ToBatchId = clean.BatchId, ChangedAt = DateTime.UtcNow });
            var when = input.ChargeNewBatchThisMonth == true ? "new fees from this month" : "new fees from next month";
            await audit.AddAsync(AuditActions.BatchChanged, "Student", s.Id,
                $"{s.Name} moved from {names.GetValueOrDefault(oldBatch)} to {names.GetValueOrDefault(clean.BatchId)} ({when})",
                null, null, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        // Moved mid-month: this month's bills stay at the old batch's fees unless the office chose otherwise.
        if (moved && input.ChargeNewBatchThisMonth == true)
            await billing.MoveThisMonthToBatchAsync(s.Id, clean.BatchId, ct);
        await billing.RaiseAsync([s.Id], ct);   // newly chosen optional fees / fees only the new batch has
        if (oldPhone != clean.Phone) await billing.RepriceFamiliesAsync([oldPhone, clean.Phone], ct);   // both families changed
        return ToDto(s);
    }

    /// <summary>
    /// POST /students/{id}/leave { leftOn, reason, note, dues, waiveReason, keepReminding } → LeaveResult
    /// The student is marked as left; everything about them is kept (fees, receipts, attendance).
    ///  - Bills for months AFTER the leaving month that have no money against them are cancelled.
    ///  - Bills up to the leaving month stay owed (dues = Keep) — or are written off (dues = Waive, with a reason).
    /// Written to the activity log. Undo with POST /students/{id}/rejoin.
    /// </summary>
    [HttpPost("{id:guid}/leave")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<LeaveResult>> Leave(Guid id, LeaveInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });
        if (!s.IsActive) return BadRequest(new { message = $"{s.Name} is already marked as left." });

        var today = Clock.Today;
        var leftOn = today;
        if (!string.IsNullOrWhiteSpace(input.LeftOn) && !DateOnly.TryParse(input.LeftOn, out leftOn))
            return BadRequest(new { message = "Enter a valid leaving date." });
        if (leftOn > today) return BadRequest(new { message = "The leaving date can't be in the future." });
        // (A student whose joining date is still ahead can be removed today.)
        if (leftOn < s.JoinedOn && s.JoinedOn <= today) return BadRequest(new { message = "The leaving date can't be before the joining date." });

        var reason = string.IsNullOrWhiteSpace(input.Reason) ? "Left" : input.Reason.Trim();
        if (!LeftReasons.Contains(reason)) return BadRequest(new { message = "Choose why the student is leaving." });
        var note = (input.Note ?? "").Trim();
        if (note.Length > 200) return BadRequest(new { message = "Keep the note under 200 letters." });

        var waive = string.Equals(input.Dues, "Waive", StringComparison.OrdinalIgnoreCase);
        var waiveReason = (input.WaiveReason ?? "").Trim();
        if (waive && waiveReason.Length < MinReasonLength)
            return BadRequest(new { message = $"Write why the pending fees are being waived (at least {MinReasonLength} letters)." });
        if (waiveReason.Length > 300) return BadRequest(new { message = "Keep the reason under 300 letters." });

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var open = await db.FeeDues
            .Where(d => d.StudentId == id && (d.Status == DueStatus.Pending || d.Status == DueStatus.Partial))
            .ToListAsync(ct);
        var leftMonth = leftOn.ToString("yyyy-MM");
        var now = DateTime.UtcNow;
        int cancelled = 0, waived = 0;
        var waivedBills = new List<object>();

        foreach (var d in open)
        {
            var balance = (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount;
            var afterLeaving = string.CompareOrdinal(d.Month, leftMonth) > 0;

            if (afterLeaving && d.PaidAmount == 0)
            {
                // Raised for a month they were no longer here: never owed.
                d.Status = DueStatus.Cancelled;
                d.CancelledAt = now;
                cancelled++;
            }
            else if (waive)
            {
                waivedBills.Add(new { d.Id, d.HeadName, d.PeriodLabel, Amount = balance });
                waived += balance;
                if (d.PaidAmount == 0)
                {
                    d.Status = DueStatus.Cancelled;
                    d.CancelledAt = now;
                }
                else
                {
                    // Part-paid: the rest is written off as a discount, so the bill closes at what was paid.
                    d.Discount = d.GrossAmount - d.PaidAmount;
                    d.Status = DueStatus.Paid;
                }
            }
        }
        var stillOwed = waive ? 0 : open.Where(d => d.Status != DueStatus.Cancelled)
            .Sum(d => (d.Amount ?? d.GrossAmount - d.Discount) - d.PaidAmount);

        s.IsActive = false;
        s.LeftOn = leftOn;
        s.LeftReason = reason;
        s.LeftNote = note.Length > 0 ? note : null;
        s.RemindAfterLeaving = stillOwed > 0 && input.KeepReminding != false;
        s.UpdatedAt = now;

        var batchName = await db.Batches.Where(b => b.Id == s.BatchId).Select(b => b.Name).FirstOrDefaultAsync(ct) ?? "";
        var owedText = stillOwed > 0 ? $" · {Money.Inr(stillOwed)} still owed" : "";
        await audit.AddAsync(AuditActions.StudentLeft, "Student", s.Id,
            $"{s.Name} ({batchName}) marked as {ReasonText(reason)} on {leftOn:dd/MM/yyyy}{owedText}",
            note.Length > 0 ? note : null,
            new { LeftOn = leftOn.ToString("yyyy-MM-dd"), Reason = reason, BillsCancelled = cancelled, StillOwed = stillOwed, s.RemindAfterLeaving },
            ct);
        if (waived > 0)
            await audit.AddAsync(AuditActions.FeesWaived, "Student", s.Id,
                $"Waived {Money.Inr(waived)} of pending fees for {s.Name} ({batchName}) on leaving",
                waiveReason, new { Total = waived, Bills = waivedBills }, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await billing.RepriceFamiliesAsync([s.ParentPhone], ct);   // the "first child" may be another one now
        return new LeaveResult(ToDto(s), cancelled, stillOwed, waived);
    }

    /// <summary>
    /// POST /students/{id}/rejoin { batchId } → Student
    /// Brings back a student who left (also the undo for "marked as left by mistake"). Bills start again from this
    /// month; months they were away are not billed. Counts against the plan's student limit.
    /// </summary>
    [HttpPost("{id:guid}/rejoin")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> Rejoin(Guid id, RejoinInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });
        if (s.IsActive) return ToDto(s);

        var batchId = s.BatchId;
        if (!string.IsNullOrWhiteSpace(input.BatchId) && !Guid.TryParse(input.BatchId, out batchId))
            return BadRequest(new { message = "Choose a batch or class." });
        var batchName = await db.Batches.Where(b => b.Id == batchId).Select(b => b.Name).FirstOrDefaultAsync(ct);
        if (batchName is null) return BadRequest(new { message = "Choose a batch or class." });

        if (await plans.StudentLimitErrorAsync(1, ct) is { } limit) return BadRequest(new { message = limit });
        if (await db.Students.AnyAsync(x => x.IsActive && x.ParentPhone == s.ParentPhone && x.Name == s.Name && x.Id != id, ct))
            return BadRequest(new { message = $"{s.Name} is already added with this number." });

        var wasLeftOn = s.LeftOn;
        s.IsActive = true;
        s.LeftOn = null;
        s.LeftReason = null;
        s.LeftNote = null;
        s.RemindAfterLeaving = false;
        s.BatchId = batchId;
        s.UpdatedAt = DateTime.UtcNow;

        await audit.AddAsync(AuditActions.StudentRejoined, "Student", s.Id,
            $"{s.Name} rejoined in {batchName}" + (wasLeftOn is DateOnly d ? $" (had left on {d:dd/MM/yyyy})" : ""),
            null, null, ct);
        await db.SaveChangesAsync(ct);

        await billing.RaiseAsync([s.Id], ct);   // this month's bills (a bill cancelled when they left is revived)
        await billing.RepriceFamiliesAsync([s.ParentPhone], ct);
        return ToDto(s);
    }

    /// <summary>
    /// DELETE /students/{id} — older apps: the same as leaving today with reason "Left" and dues kept.
    /// (It used to cancel every unpaid bill, which wiped real arrears. It no longer does.)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var active = await db.Students.AnyAsync(x => x.Id == id && x.IsActive, ct);
        if (!active) return NoContent();
        var result = await Leave(id, new LeaveInput(null, "Left", null, "Keep", null, true), ct);
        return result.Result ?? NoContent();   // a refusal (with its message) is passed on
    }

    private static string ReasonText(string reason) => reason switch
    {
        "Transferred" => "transferred",
        "PassedOut" => "passed out",
        "Removed" => "removed",
        _ => "left"
    };

    /// <summary>POST /students/{id}/concessions</summary>
    [HttpPost("{id:guid}/concessions")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> AddConcession(Guid id, ConcessionInput input, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        if (!Reasons.Contains(input.Reason)) return BadRequest(new { message = "Choose a reason for the discount." });
        if (input.Kind is not ("Percent" or "Amount")) return BadRequest(new { message = "Choose percent or amount." });
        if (input.Value <= 0) return BadRequest(new { message = "Enter a discount above zero." });
        if (input.Kind == "Percent" && input.Value > 100) return BadRequest(new { message = "A percent discount can't be more than 100." });

        Guid? headId = null;
        if (!string.IsNullOrEmpty(input.FeeHeadId))
        {
            if (!Guid.TryParse(input.FeeHeadId, out var h) || !await db.FeeHeads.AnyAsync(x => x.Id == h && !x.IsDeleted && x.Kind == null, ct))
                return BadRequest(new { message = "That fee no longer exists." });
            headId = h;
        }

        s.StudentConcessions.Add(new StudentConcession
        {
            TenantId = s.TenantId,
            Reason = input.Reason,
            Note = input.Note?.Trim() ?? "",
            FeeHeadId = headId,
            Kind = input.Kind,
            Value = input.Value,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await billing.RepriceAsync(s.Id, ct);
        return ToDto(s);
    }

    /// <summary>DELETE /students/{id}/concessions/{concessionId}</summary>
    [HttpDelete("{id:guid}/concessions/{concessionId:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<StudentDto>> RemoveConcession(Guid id, Guid concessionId, CancellationToken ct)
    {
        var s = await WithDetails().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Student not found." });

        var c = s.StudentConcessions.FirstOrDefault(x => x.Id == concessionId);
        if (c is not null)
        {
            db.StudentConcessions.Remove(c);
            await db.SaveChangesAsync(ct);
            await billing.RepriceAsync(s.Id, ct);
        }
        return ToDto(s);
    }

    // ---------- helpers ----------

    private record CleanInput(string Name, string ParentName, string Phone, Guid BatchId, DateOnly JoinedOn, List<Guid> OptionalHeadIds);

    private async Task<(CleanInput?, string?)> Validate(StudentInput i, Guid? editingId, CancellationToken ct)
    {
        var name = i.Name?.Trim() ?? "";
        if (name.Length == 0) return (null, "Enter the student's name.");

        var phone = Phone.Normalise(i.ParentPhone);
        if (phone is null) return (null, "Enter a valid 10-digit mobile number.");

        if (!Guid.TryParse(i.BatchId, out var batchId) || !await db.Batches.AnyAsync(b => b.Id == batchId, ct))
            return (null, "Choose a batch or class.");

        var joinedOn = Clock.Today;
        if (!string.IsNullOrWhiteSpace(i.JoinedOn) && !DateOnly.TryParse(i.JoinedOn, out joinedOn))
            return (null, "Enter a valid joining date.");

        var headIds = new List<Guid>();
        foreach (var raw in i.OptionalHeadIds ?? [])
        {
            if (!Guid.TryParse(raw, out var h)) return (null, "One of the chosen fees no longer exists.");
            headIds.Add(h);
        }
        headIds = headIds.Distinct().ToList();
        if (headIds.Count > 0 &&
            await db.FeeHeads.CountAsync(h => headIds.Contains(h.Id) && h.IsOptional && !h.IsDeleted, ct) != headIds.Count)
            return (null, "One of the chosen fees no longer exists.");

        if (await db.Students.AnyAsync(s => s.IsActive && s.ParentPhone == phone && s.Name == name && s.Id != editingId, ct))
            return (null, $"{name} is already added with this number.");

        return (new CleanInput(name, i.ParentName?.Trim() ?? "", phone, batchId, joinedOn, headIds), null);
    }

    private static void Apply(Student s, CleanInput c)
    {
        s.Name = c.Name;
        s.ParentName = c.ParentName;
        s.ParentPhone = c.Phone;
        s.BatchId = c.BatchId;
        s.JoinedOn = c.JoinedOn;
        s.UpdatedAt = DateTime.UtcNow;
    }

    private Task<List<FeeHead>> LoadOptionalHeads(List<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult(new List<FeeHead>())
                       : db.FeeHeads.Where(h => ids.Contains(h.Id)).ToListAsync(ct);

    /// <summary>Bills with no money against them are cancelled; part-paid bills stay so the balance is still owed.</summary>
    private Task<int> CancelUnpaidBills(Guid studentId, List<Guid>? onlyHeads, CancellationToken ct) =>
        db.FeeDues
          .Where(d => d.StudentId == studentId && d.PaidAmount == 0 && d.Status != "Cancelled"
                      && (onlyHeads == null || onlyHeads.Contains(d.FeeHeadId)))
          .ExecuteUpdateAsync(u => u
              .SetProperty(d => d.Status, "Cancelled")
              .SetProperty(d => d.CancelledAt, DateTime.UtcNow), ct);

    private IQueryable<Student> WithDetails() =>
        db.Students.Include(s => s.FeeHeads).Include(s => s.StudentConcessions);

    private Task<bool> TeacherCanSee(Guid batchId, CancellationToken ct) =>
        db.Batches.AnyAsync(b => b.Id == batchId && b.Users.Any(u => u.Id == current.UserId), ct);

    /// <summary>Random, unguessable id for the parent payment link (/pay/{token}).</summary>
    internal static string NewPayToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    internal static StudentDto ToDto(Student s) => new(
        s.Id.ToString(), s.Name, s.ParentName, s.ParentPhone, s.BatchId.ToString(),
        s.JoinedOn.ToString("yyyy-MM-dd"), s.IsActive,
        s.FeeHeads.Select(h => h.Id.ToString()).ToArray(),
        s.StudentConcessions.OrderBy(c => c.CreatedAt)
            .Select(c => new ConcessionDto(c.Id.ToString(), c.Reason, c.Note, c.FeeHeadId?.ToString(), c.Kind, c.Value))
            .ToArray(),
        s.AdmissionNo, null, s.IsNewAdmission,
        s.IsActive ? null : s.LeftOn?.ToString("yyyy-MM-dd"), s.LeftReason, s.LeftNote, s.RemindAfterLeaving);
}