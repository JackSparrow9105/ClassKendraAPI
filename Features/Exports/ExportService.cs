using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Exports;

/// <summary>
/// Builds the Excel files for Download (Settings › Download to Excel) and "Download all my data".
/// Everything goes through the normal centre filter, so a centre only ever gets its own rows.
/// Money is in whole rupees; dates and times are India time.
/// </summary>
public class ExportService(ClassKendraContext db, AdvanceService advances)
{
    private const XlsxCol T = XlsxCol.Text, N = XlsxCol.Number, M = XlsxCol.Money, D = XlsxCol.Date, DT = XlsxCol.DateTime;

    private static DateTime Ist(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).AddMinutes(330);
    private static DateTime UtcStart(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).AddMinutes(-330);

    private string? _group;
    /// <summary>"Class" for schools, "Batch" for coaching centres.</summary>
    private async Task<string> GroupAsync(CancellationToken ct) =>
        _group ??= await db.Tenants.Select(t => t.InstitutionType).FirstOrDefaultAsync(ct) == "School" ? "Class" : "Batch";

    private static string LeftText(string? reason) => reason switch
    {
        "Transferred" => "Transferred", "PassedOut" => "Passed out", "Removed" => "Removed", null => "", _ => "Left"
    };

    // ---------------- students ----------------

    /// <summary>Every student (or only active / only left), with what they owe and any advance.</summary>
    public async Task StudentsAsync(XlsxWriter x, string? status, CancellationToken ct)
    {
        var group = await GroupAsync(ct);
        var q = db.Students.AsNoTracking();
        if (status == "Active") q = q.Where(s => s.IsActive);
        else if (status == "Left") q = q.Where(s => !s.IsActive);

        var rows = await q.OrderBy(s => s.Batch.Name).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.AdmissionNo, s.Name, Batch = s.Batch.Name, s.ParentName, s.ParentPhone, s.JoinedOn,
                s.IsNewAdmission, s.IsActive, s.LeftOn, s.LeftReason, s.LeftNote,
                Optional = s.FeeHeads.Select(h => h.Name).ToList(),
                Concessions = s.StudentConcessions.Select(c => new { c.Reason, c.Kind, c.Value }).ToList()
            })
            .ToListAsync(ct);

        var owed = await db.FeeDues.AsNoTracking()
            .Where(d => d.Status == DueStatus.Pending || d.Status == DueStatus.Partial)
            .GroupBy(d => d.StudentId)
            .Select(g => new { g.Key, Owed = g.Sum(d => d.GrossAmount - d.Discount - d.PaidAmount) })
            .ToDictionaryAsync(r => r.Key, r => r.Owed, ct);
        var adv = await advances.ForAsync(rows.Select(r => r.Id).ToList(), ct);

        var sheet = x.Sheet("Students",
            ("Admission no", 14, T), ("Name", 24, T), (group, 16, T), ("Parent name", 20, T), ("Parent phone", 14, T),
            ("Joined on", 12, D), ("New admission", 10, T), ("Status", 10, T), ("Left on", 12, D), ("Why left", 12, T),
            ("Note", 24, T), ("Opted in fees", 20, T), ("Discounts", 24, T), ("Balance due", 12, M), ("Advance", 12, M));
        foreach (var s in rows)
            sheet.Add(s.AdmissionNo, s.Name, s.Batch, s.ParentName, s.ParentPhone, s.JoinedOn, s.IsNewAdmission,
                s.IsActive ? "Studying" : "Left", s.IsActive ? null : s.LeftOn, LeftText(s.LeftReason), s.LeftNote,
                string.Join(", ", s.Optional),
                string.Join(", ", s.Concessions.Select(c => $"{c.Reason} {(c.Kind == "Percent" ? c.Value + "%" : "₹" + c.Value)}")),
                owed.GetValueOrDefault(s.Id), adv.GetValueOrDefault(s.Id));
    }

    // ---------------- fees (bills) ----------------

    /// <summary>Bills raised in months from..to (by the month they were raised for), removed ones included and marked.</summary>
    public async Task FeesAsync(XlsxWriter x, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var group = await GroupAsync(ct);
        string f = from.ToString("yyyy-MM"), t = to.ToString("yyyy-MM");
        var rows = await db.FeeDues.AsNoTracking()
            .Where(d => string.Compare(d.Month, f) >= 0 && string.Compare(d.Month, t) <= 0)
            .OrderBy(d => d.Month).ThenBy(d => d.Student.Name).ThenBy(d => d.DueDate)
            .Select(d => new
            {
                d.Student.AdmissionNo, Student = d.Student.Name, Batch = d.Batch.Name, d.HeadName, d.Kind, d.PeriodLabel, d.Month,
                d.DueDate, d.GrossAmount, d.Discount, d.PaidAmount, d.Status, d.LastPaidOn, d.RemindersSent
            })
            .ToListAsync(ct);

        var sheet = x.Sheet("Fees",
            ("Admission no", 14, T), ("Student", 24, T), (group, 16, T), ("Fee", 20, T), ("Type", 14, T), ("Period", 18, T),
            ("Month", 9, T), ("Due date", 12, D), ("Fee amount", 12, M), ("Discount / waived", 12, M), ("To pay", 12, M),
            ("Paid", 12, M), ("Balance", 12, M), ("Status", 10, T), ("Last paid on", 12, D), ("Reminders sent", 10, N));
        foreach (var d in rows)
        {
            var amount = d.GrossAmount - d.Discount;
            var cancelled = d.Status == DueStatus.Cancelled;
            sheet.Add(d.AdmissionNo, d.Student, d.Batch, d.HeadName, KindText(d.Kind), d.PeriodLabel, d.Month, d.DueDate,
                d.GrossAmount, d.Discount, amount, d.PaidAmount, cancelled ? 0 : amount - d.PaidAmount,
                cancelled ? "Removed" : d.Status, d.LastPaidOn, d.RemindersSent);
        }
    }

    private static string KindText(string? kind) => kind switch
    {
        BillKinds.LateFee => "Late fee", BillKinds.OpeningBalance => "Previous dues", BillKinds.Charge => "Extra charge", _ => "Fee"
    };

    // ---------------- receipts ----------------

    /// <summary>Receipts paid from..to, cancelled ones included and marked. includeOpening adds "advance before ClassKendra".</summary>
    public async Task ReceiptsAsync(XlsxWriter x, DateOnly from, DateOnly to, bool includeOpening, CancellationToken ct)
    {
        var group = await GroupAsync(ct);
        var q = db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled]).AsNoTracking()
            .Where(p => p.PaidOn >= from && p.PaidOn <= to);
        if (!includeOpening) q = q.Where(p => p.Mode != PaymentModes.Opening);
        var rows = await q.OrderBy(p => p.PaidOn).ThenBy(p => p.ReceiptNo)
            .Select(p => new
            {
                p.ReceiptNo, p.PaidOn, p.Student.AdmissionNo, Student = p.Student.Name, Batch = p.Student.Batch.Name,
                p.Amount, p.Mode, p.Note, p.ReceivedByName, p.CreatedAt, p.CancelledAt, p.CancelledByName, p.CancelReason,
                Lines = p.PaymentAllocations.Select(a => new { a.FeeDue.HeadName, a.FeeDue.PeriodLabel, a.Amount }).ToList()
            })
            .ToListAsync(ct);

        var sheet = x.Sheet("Receipts",
            ("Receipt no", 14, T), ("Date", 12, D), ("Admission no", 14, T), ("Student", 24, T), (group, 16, T),
            ("Amount", 12, M), ("Mode", 9, T), ("For", 40, T), ("Advance (not on a fee)", 12, M), ("Note", 20, T),
            ("Received by", 16, T), ("Entered at", 16, DT), ("Cancelled", 9, T), ("Cancelled by", 16, T), ("Why cancelled", 30, T));
        foreach (var p in rows)
        {
            var onFees = p.Lines.Sum(l => l.Amount);
            sheet.Add(p.ReceiptNo, p.PaidOn, p.AdmissionNo, p.Student, p.Batch, p.Amount,
                p.Mode == PaymentModes.Opening ? "Before ClassKendra" : p.Mode,
                string.Join("; ", p.Lines.Select(l => $"{l.HeadName} {l.PeriodLabel} ₹{l.Amount}")),
                p.CancelledAt is null ? Math.Max(0, p.Amount - onFees) : 0,
                p.Note, p.ReceivedByName, Ist(p.CreatedAt), p.CancelledAt is not null, p.CancelledByName, p.CancelReason);
        }
    }

    // ---------------- refunds and fee changes ----------------

    public async Task RefundsAsync(XlsxWriter x, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Refunds.AsNoTracking()
            .Where(r => r.RefundedOn >= from && r.RefundedOn <= to)
            .OrderBy(r => r.RefundedOn).ThenBy(r => r.RefundNo)
            .Join(db.Students, r => r.StudentId, s => s.Id, (r, s) => new { r, s.Name, s.AdmissionNo })
            .ToListAsync(ct);
        var sheet = x.Sheet("Refunds",
            ("Refund no", 14, T), ("Date", 12, D), ("Admission no", 14, T), ("Student", 24, T), ("Amount", 12, M),
            ("Mode", 9, T), ("Why", 40, T), ("By", 16, T), ("Entered at", 16, DT));
        foreach (var x2 in rows)
            sheet.Add(x2.r.RefundNo, x2.r.RefundedOn, x2.AdmissionNo, x2.Name, x2.r.Amount, x2.r.Mode, x2.r.Reason, x2.r.ByName, Ist(x2.r.CreatedAt));
    }

    public async Task AdjustmentsAsync(XlsxWriter x, CancellationToken ct)
    {
        var rows = await db.FeeAdjustments.AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .Join(db.FeeDues, a => a.FeeDueId, d => d.Id, (a, d) => new { a, d.HeadName, d.PeriodLabel, Student = d.Student.Name, d.Student.AdmissionNo })
            .ToListAsync(ct);
        var sheet = x.Sheet("Fee changes",
            ("Date", 16, DT), ("Admission no", 14, T), ("Student", 24, T), ("What", 12, T), ("Fee", 20, T), ("Period", 18, T),
            ("Amount", 12, M), ("Why", 40, T), ("By", 16, T));
        foreach (var r in rows)
            sheet.Add(Ist(r.a.CreatedAt), r.AdmissionNo, r.Student, r.a.Kind == AdjustmentKinds.Waive ? "Waived" : "Charge added",
                r.HeadName, r.PeriodLabel, r.a.Amount, r.a.Reason, r.a.ByName);
    }

    // ---------------- day-book ----------------

    /// <summary>
    /// Day-book: every receipt (money in) and refund (money out) from..to, then a summary per day by mode —
    /// what the accountant needs to match the cash box and the bank.
    /// </summary>
    public async Task DayBookAsync(XlsxWriter x, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var receipts = await db.Payments.AsNoTracking()
            .Where(p => p.PaidOn >= from && p.PaidOn <= to && p.Mode != PaymentModes.Opening)
            .Select(p => new { Date = p.PaidOn, No = p.ReceiptNo, Student = p.Student.Name, p.Mode, In = p.Amount, Note = p.Note, p.ReceivedByName })
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.RefundedOn >= from && r.RefundedOn <= to)
            .Join(db.Students, r => r.StudentId, s => s.Id, (r, s) => new { Date = r.RefundedOn, No = r.RefundNo, Student = s.Name, r.Mode, Out = r.Amount, r.Reason, r.ByName })
            .ToListAsync(ct);

        var lines = receipts.Select(r => (r.Date, Type: "Receipt", r.No, r.Student, r.Mode, In: r.In, Out: 0, Note: r.Note ?? "", By: r.ReceivedByName ?? ""))
            .Concat(refunds.Select(r => (r.Date, Type: "Refund", r.No, r.Student, r.Mode, In: 0, Out: r.Out, Note: r.Reason, By: r.ByName)))
            .OrderBy(l => l.Date).ThenBy(l => l.Type).ThenBy(l => l.No)
            .ToList();

        var book = x.Sheet("Day-book",
            ("Date", 12, D), ("Type", 9, T), ("No", 14, T), ("Student", 24, T), ("Mode", 9, T), ("Money in", 12, M),
            ("Money out", 12, M), ("Note", 30, T), ("By", 16, T));
        foreach (var l in lines) book.Add(l.Date, l.Type, l.No, l.Student, l.Mode, l.In == 0 ? null : l.In, l.Out == 0 ? null : l.Out, l.Note, l.By);

        var modes = new[] { "Cash", "UPI", "Cheque", "Online" };
        var cols = new List<(string, double, XlsxCol)> { ("Date", 12, D) };
        cols.AddRange(modes.Select(m => ($"{m} in", 12.0, M)));
        cols.AddRange([("Total in", 12, M), ("Refunds out", 12, M), ("Net", 12, M), ("Cash in hand change", 14, M)]);
        var summary = x.Sheet("Daily totals", [.. cols]);
        foreach (var day in lines.GroupBy(l => l.Date).OrderBy(g => g.Key))
        {
            var row = new List<object?> { day.Key };
            row.AddRange(modes.Select(m => (object)day.Where(l => l.Mode == m).Sum(l => l.In)));
            var totalIn = day.Sum(l => l.In);
            var totalOut = day.Sum(l => l.Out);
            var cash = day.Where(l => l.Mode == "Cash").Sum(l => l.In - l.Out);
            row.AddRange([totalIn, totalOut, totalIn - totalOut, cash]);
            summary.Add([.. row]);
        }
    }

    // ---------------- the rest of "all my data" ----------------

    public async Task SetupAsync(XlsxWriter x, CancellationToken ct)
    {
        var group = await GroupAsync(ct);
        var centre = await db.Tenants.AsNoTracking().FirstAsync(ct);
        x.Sheet("Centre", ("Field", 22, T), ("Value", 40, T))
            .Add("Name", centre.Name).Add("Owner", centre.OwnerName).Add("City", centre.City).Add("Address", centre.Address)
            .Add("Phone", centre.Phone).Add("Type", centre.InstitutionType).Add("Plan", centre.PlanCode)
            .Add("Using ClassKendra since", Ist(centre.CreatedAt).ToString("dd-MM-yyyy"));

        var batches = await db.Batches.AsNoTracking().OrderBy(b => b.Name)
            .Select(b => new { b.Name, b.ClassName, b.Section, b.Days, b.StartTime, b.EndTime, Students = b.Students.Count(s => s.IsActive) })
            .ToListAsync(ct);
        var bs = x.Sheet(group == "Class" ? "Classes" : "Batches",
            (group, 20, T), ("Class", 10, T), ("Section", 10, T), ("Days", 16, T), ("Starts", 8, T), ("Ends", 8, T), ("Students", 10, N));
        foreach (var b in batches) bs.Add(b.Name, b.ClassName, b.Section, b.Days, b.StartTime?.ToString("HH:mm"), b.EndTime?.ToString("HH:mm"), b.Students);

        var heads = await db.FeeHeads.AsNoTracking().Where(h => !h.IsDeleted && h.Kind == null).OrderBy(h => h.CreatedAt)
            .Select(h => new { h.Name, h.Frequency, h.IsOptional, h.IsAdmissionFee, Amounts = h.FeeHeadAmounts.Select(a => new { a.Batch.Name, a.Amount }).ToList() })
            .ToListAsync(ct);
        var fs = x.Sheet("Fee setup", ("Fee", 20, T), ("How often", 12, T), ("Who pays", 12, T), (group, 20, T), ("Amount", 12, M));
        foreach (var h in heads)
            foreach (var a in h.Amounts.OrderBy(a => a.Name))
                fs.Add(h.Name, h.Frequency, h.IsAdmissionFee ? "New admissions" : h.IsOptional ? "Opted in" : "Everyone", a.Name, a.Amount);
    }

    public async Task StaffAsync(XlsxWriter x, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking().OrderBy(u => u.Role).ThenBy(u => u.Name)
            .Select(u => new { u.Name, u.Phone, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt, Batches = u.Batches.Select(b => b.Name).ToList() })
            .ToListAsync(ct);
        var s = x.Sheet("Staff", ("Name", 22, T), ("Phone", 14, T), ("Role", 10, T), ("Active", 8, T), ("Added", 16, DT), ("Last sign-in", 16, DT), ("Batches / classes", 30, T));
        foreach (var u in rows)
            s.Add(u.Name, u.Phone, u.Role == "Staff" ? "Office" : u.Role, u.IsActive, Ist(u.CreatedAt), u.LastLoginAt is DateTime l ? Ist(l) : null, string.Join(", ", u.Batches));
    }

    /// <summary>Student attendance: one row per register (batch and day) with who was absent.</summary>
    public async Task AttendanceAsync(XlsxWriter x, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var group = await GroupAsync(ct);
        var rows = await db.AttendanceSheets.AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date).ThenBy(a => a.Batch.Name)
            .Select(a => new
            {
                a.Date, Batch = a.Batch.Name, a.PresentCount, Absent = a.AttendanceAbsences.Select(s => s.Student.Name).ToList(),
                MarkedBy = a.MarkedByUser != null ? a.MarkedByUser.Name : "", a.SavedAt
            })
            .ToListAsync(ct);
        var s = x.Sheet("Attendance", ("Date", 12, D), (group, 18, T), ("Present", 9, N), ("Absent", 9, N), ("Absent students", 50, T), ("Marked by", 16, T), ("Saved at", 16, DT));
        foreach (var r in rows) s.Add(r.Date, r.Batch, r.PresentCount, r.Absent.Count, string.Join(", ", r.Absent.Order()), r.MarkedBy, Ist(r.SavedAt));
    }

    public async Task StaffAttendanceAsync(XlsxWriter x, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.StaffAttendance.AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date).ThenBy(a => a.User.Name)
            .Select(a => new { a.Date, a.User.Name, a.Status, a.CheckInAt, a.CheckOutAt, a.LateMinutes, a.DistanceMeters, ByOwner = a.MarkedByUserId != null })
            .ToListAsync(ct);
        var s = x.Sheet("Staff attendance", ("Date", 12, D), ("Name", 22, T), ("Status", 9, T), ("Check-in", 16, DT), ("Check-out", 16, DT), ("Minutes late", 9, N), ("Metres from school", 10, N), ("Marked by owner", 9, T));
        foreach (var r in rows)
            s.Add(r.Date, r.Name, r.Status, r.CheckInAt is DateTime i ? Ist(i) : null, r.CheckOutAt is DateTime o ? Ist(o) : null, r.LateMinutes, r.DistanceMeters, r.ByOwner);

        var leaves = await db.StaffLeaves.AsNoTracking().OrderBy(l => l.FromDate)
            .Select(l => new { l.User.Name, l.FromDate, l.ToDate, l.Days, l.Reason, l.Status, l.RejectReason })
            .ToListAsync(ct);
        var ls = x.Sheet("Staff leave", ("Name", 22, T), ("From", 12, D), ("To", 12, D), ("Days", 6, N), ("Reason", 30, T), ("Status", 10, T), ("Why rejected", 24, T));
        foreach (var l in leaves) ls.Add(l.Name, l.FromDate, l.ToDate, l.Days, l.Reason, l.Status, l.RejectReason);
    }

    public async Task HolidaysAsync(XlsxWriter x, CancellationToken ct)
    {
        var rows = await db.Holidays.AsNoTracking().OrderBy(h => h.FromDate).ToListAsync(ct);
        var s = x.Sheet("Holidays", ("Name", 24, T), ("From", 12, D), ("To", 12, D));
        foreach (var h in rows) s.Add(h.Name, h.FromDate, h.ToDate);
    }

    public async Task MessagesAsync(XlsxWriter x, DateOnly from, CancellationToken ct)
    {
        var since = UtcStart(from);
        var rows = await db.Messages.AsNoTracking().Where(m => m.CreatedAt >= since).OrderBy(m => m.CreatedAt)
            .Select(m => new { m.CreatedAt, m.RecipientName, m.Phone, m.Type, m.Status, m.Text, m.Error })
            .ToListAsync(ct);
        var s = x.Sheet("WhatsApp messages", ("Sent at", 16, DT), ("To", 22, T), ("Phone", 14, T), ("Type", 12, T), ("Status", 10, T), ("Message", 60, T), ("Error", 24, T));
        foreach (var m in rows) s.Add(Ist(m.CreatedAt), m.RecipientName, m.Phone, m.Type, m.Status, m.Text, m.Error);
    }

    public async Task ActivityAsync(XlsxWriter x, CancellationToken ct)
    {
        var rows = await db.AuditLogs.AsNoTracking().OrderBy(a => a.At).ToListAsync(ct);
        var s = x.Sheet("Activity log", ("When", 16, DT), ("Who", 18, T), ("Role", 9, T), ("What", 18, T), ("Details", 60, T), ("Reason", 40, T));
        foreach (var a in rows) s.Add(Ist(a.At), a.UserName, a.UserRole, a.Action, a.Summary, a.Reason);
    }
}
