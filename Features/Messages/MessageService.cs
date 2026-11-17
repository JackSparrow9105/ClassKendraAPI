using System.Globalization;
using System.Text.Json;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Messages;

public static class MessageTypes
{
    public const string FeeReminder = "FeeReminder";
    public const string AbsentAlert = "AbsentAlert";
    public const string Receipt = "Receipt";
    public const string StaffInvite = "StaffInvite";
    public const string Admission = "Admission";
    public const string LeaveRequest = "LeaveRequest";
    public const string LeaveAnswer = "LeaveAnswer";
}

public static class MessageStatus
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Delivered = "Delivered";
    public const string Read = "Read";
    public const string Failed = "Failed";
}

/// <summary>
/// Puts WhatsApp messages in the outbox (Messages table). OutboxWorker sends them.
/// Every message counts against the centre's monthly limit; when the limit is reached nothing is queued.
///
/// Texts match core/messages/templates.ts in the Angular app. Each one must be an approved
/// template in Meta Business Manager (English "en" and Hindi "hi"); the parameters are listed
/// in the order they appear in that language's text:
///   fee_reminder          en: student, amount, centre, due text, link   hi: centre, student, amount, due text, link
///   absent_alert          en: student, centre, date                     hi: student, date, centre
///   payment_receipt       en: centre, amount, student, receipt no       hi: centre, student, amount, receipt no
///   payment_receipt_due   same as payment_receipt + balance
///   staff_invite          (English only) name, centre, link
///   admission_confirmation en: centre, student, batch, admission no, fees text   hi: centre, student, batch, admission no, fees text
///
/// Free plan (tuition / coaching after the trial): the same templates with "_free" at the end of the name.
/// They end with the footer "Powered by ClassKendra", and fee_reminder_free has no payment link
/// (parameters: the same as fee_reminder without the link; text ends "Please pay at the centre.").
/// </summary>
public class MessageService(ClassKendraContext db, CurrentUser current, IConfiguration config, PlanService plans)
{
    public const string FreeFooter = "Powered by ClassKendra";

    private (string Name, string Lang)? _centre;

    public async Task<(int Used, int Limit)> UsageAsync(CancellationToken ct)
    {
        await ResetMonthIfNeeded(ct);
        var t = await db.Tenants.AsNoTracking().Select(x => new { x.MessagesUsed, x.MessageLimit }).FirstAsync(ct);
        return (t.MessagesUsed, t.MessageLimit);
    }

    /// <summary>The centre's message settings (receipts on/off, absent alert rule).</summary>
    public async Task<(bool SendReceipts, string AbsentAlerts)> SettingsAsync(CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().Select(x => new { x.SendReceipts, x.AbsentAlerts }).FirstAsync(ct);
        return (t.SendReceipts, t.AbsentAlerts ?? AbsentAlertModes.Every);
    }

    /// <summary>
    /// WhatsApp receipt to the parent. Skipped when the centre has turned receipts off,
    /// unless force is true (the staff member ticked "send receipt" for this payment).
    /// </summary>
    public async Task<bool> QueueReceiptAsync(PaymentView p, CancellationToken ct, bool force = false)
    {
        if (!force && !(await SettingsAsync(ct)).SendReceipts) return false;

        var (org, lang) = await Centre(ct);
        var amount = Money.Inr(p.Amount);
        var balance = p.BalanceAfter > 0 ? Money.Inr(p.BalanceAfter) : null;

        var text = lang == "hi"
            ? $"{org}: {p.StudentName} की {amount} फीस प्राप्त हुई। रसीद {p.ReceiptNo}।{(balance is null ? "" : $" बाकी: {balance}।")} धन्यवाद!"
            : $"{org}: received {amount} for {p.StudentName}. Receipt {p.ReceiptNo}.{(balance is null ? "" : $" Balance due: {balance}.")} Thank you!";

        string[] args = lang == "hi" ? [org, p.StudentName, amount, p.ReceiptNo] : [org, amount, p.StudentName, p.ReceiptNo];
        if (balance is not null) args = [.. args, balance];

        return await QueueAsync(Guid.Parse(p.StudentId), $"{p.StudentName}'s parent", p.ParentPhone, MessageTypes.Receipt,
            balance is null ? "payment_receipt" : "payment_receipt_due", args, text, lang, ct);
    }

    /// <summary>One message per family with everything unpaid. oldestDue decides "12 days late" or "due 10/11".</summary>
    public async Task<bool> QueueFeeReminderAsync(Guid studentId, string studentName, string parentName, string phone,
        int amount, DateOnly oldestDue, string payLink, CancellationToken ct)
    {
        var (org, lang) = await Centre(ct);
        var late = Clock.Today.DayNumber - oldestDue.DayNumber;
        var due = $"{oldestDue.Day:D2}/{oldestDue.Month:D2}";
        var dueText = late > 0
            ? (lang == "hi" ? $"{late} दिन देर" : $"{late} days late")
            : (lang == "hi" ? $"देय {due}" : $"due {due}");
        var amt = Money.Inr(amount);

        // Free plan: no online payment link — parents pay at the centre.
        if (await plans.IsFreeAsync(ct))
        {
            var freeText = lang == "hi"
                ? $"नमस्ते! {org} में {studentName} की {amt} फीस बाकी है ({dueText})। कृपया सेंटर पर भुगतान करें।"
                : $"Namaste! {studentName}'s fees of {amt} at {org} are due ({dueText}). Please pay at the centre.";
            string[] freeArgs = lang == "hi" ? [org, studentName, amt, dueText] : [studentName, amt, org, dueText];
            return await QueueAsync(studentId, Recipient(parentName, studentName), phone, MessageTypes.FeeReminder,
                "fee_reminder", freeArgs, freeText, lang, ct);
        }

        var text = lang == "hi"
            ? $"नमस्ते! {org} में {studentName} की {amt} फीस बाकी है ({dueText})। यहाँ भुगतान करें: {payLink}"
            : $"Namaste! {studentName}'s fees of {amt} at {org} are due ({dueText}). Pay here: {payLink}";
        string[] args = lang == "hi" ? [org, studentName, amt, dueText, payLink] : [studentName, amt, org, dueText, payLink];

        return await QueueAsync(studentId, Recipient(parentName, studentName), phone, MessageTypes.FeeReminder,
            "fee_reminder", args, text, lang, ct);
    }

    public async Task<bool> QueueAbsentAlertAsync(Guid studentId, string studentName, string parentName, string phone,
        DateOnly date, CancellationToken ct)
    {
        var (org, lang) = await Centre(ct);
        var d = LongDate(date);
        var text = lang == "hi"
            ? $"नमस्ते! {studentName} आज ({d}) {org} में अनुपस्थित है। कृपया ध्यान दें।"
            : $"Namaste! {studentName} was absent at {org} today ({d}).";
        string[] args = lang == "hi" ? [studentName, d, org] : [studentName, org, d];

        return await QueueAsync(studentId, Recipient(parentName, studentName), phone, MessageTypes.AbsentAlert,
            "absent_alert", args, text, lang, ct);
    }

    /// <summary>
    /// "Welcome to Vidya Coaching! Aarav's admission in Class 10 · Maths is confirmed. Admission no. A-2026-0045. ..."
    /// Skipped when the centre turned admission messages off, unless force is true (the "send again" button).
    /// feesText: what to pay now, already in the message language ("Fees due: ₹2,500 by 10/12"), or "" when nothing is due.
    /// </summary>
    public async Task<bool> QueueAdmissionAsync(Guid studentId, string studentName, string parentName, string phone,
        string batchName, string admissionNo, string feesText, bool force, CancellationToken ct)
    {
        if (!force && !await db.Tenants.AsNoTracking().Select(t => t.SendAdmissionMessages).FirstAsync(ct)) return false;
        var (org, lang) = await Centre(ct);
        var fees = string.IsNullOrWhiteSpace(feesText) ? (lang == "hi" ? "स्वागत है!" : "Welcome aboard!") : feesText;
        var text = lang == "hi"
            ? $"{org} में आपका स्वागत है! {studentName} का {batchName} में एडमिशन पक्का हो गया है। एडमिशन नंबर {admissionNo}। {fees}"
            : $"Welcome to {org}! {studentName}'s admission in {batchName} is confirmed. Admission no. {admissionNo}. {fees}";
        string[] args = [org, studentName, batchName, admissionNo, fees];
        return await QueueAsync(studentId, Recipient(parentName, studentName), phone, MessageTypes.Admission,
            "admission_confirmation", args, text, lang, ct);
    }

    /// <summary>"Fees due: ₹2,500 by 10/12" in the centre's message language.</summary>
    public async Task<string> FeesDueTextAsync(int amount, DateOnly? dueDate, CancellationToken ct)
    {
        if (amount <= 0) return "";
        var (_, lang) = await Centre(ct);
        var amt = Money.Inr(amount);
        // A due date already passed (joined mid-month) reads as "due now", not a date in the past.
        var by = dueDate is DateOnly d && d > Clock.Today ? $"{d.Day:D2}/{d.Month:D2}" : null;
        return lang == "hi"
            ? (by is null ? $"फीस: {amt}, अभी जमा करें।" : $"फीस: {amt}, {by} तक।")
            : (by is null ? $"Fees due now: {amt}." : $"Fees due: {amt} by {by}.");
    }

    /// <summary>WhatsApp to the owner: a teacher applied for leave (only when the school turned on WhatsApp for leave).</summary>
    public async Task<bool> QueueLeaveRequestAsync(string ownerName, string ownerPhone, string staffName, string dates,
        int days, string reason, CancellationToken ct)
    {
        var (_, lang) = await Centre(ct);
        var dayText = lang == "hi" ? $"{days} दिन" : days == 1 ? "1 working day" : $"{days} working days";
        var text = lang == "hi"
            ? $"{staffName} ने छुट्टी के लिए आवेदन किया है: {dates} ({dayText})। कारण: {reason}। मंज़ूर या नामंज़ूर करने के लिए ClassKendra खोलें।"
            : $"{staffName} applied for leave: {dates} ({dayText}). Reason: {reason}. Open ClassKendra to approve or reject.";
        return await QueueAsync(null, ownerName, ownerPhone, MessageTypes.LeaveRequest, "leave_request",
            [staffName, dates, dayText, reason], text, lang, ct);
    }

    /// <summary>WhatsApp to the teacher: their leave was approved or rejected.</summary>
    public async Task<bool> QueueLeaveAnswerAsync(string staffName, string staffPhone, string dates, bool approved,
        string? rejectReason, CancellationToken ct)
    {
        var (org, lang) = await Centre(ct);
        if (approved)
        {
            var ok = lang == "hi" ? $"{dates} की आपकी छुट्टी मंज़ूर हो गई है। — {org}" : $"Your leave for {dates} is approved. — {org}";
            return await QueueAsync(null, staffName, staffPhone, MessageTypes.LeaveAnswer, "leave_approved", [dates, org], ok, lang, ct);
        }
        var why = string.IsNullOrWhiteSpace(rejectReason) ? (lang == "hi" ? "—" : "No reason given") : rejectReason.Trim();
        var no = lang == "hi"
            ? $"{dates} की आपकी छुट्टी मंज़ूर नहीं हुई। कारण: {why}। — {org}"
            : $"Your leave for {dates} is not approved. Reason: {why}. — {org}";
        return await QueueAsync(null, staffName, staffPhone, MessageTypes.LeaveAnswer, "leave_rejected", [dates, why, org], no, lang, ct);
    }

    public async Task<bool> QueueStaffInviteAsync(string name, string phone, CancellationToken ct)
    {
        var (org, _) = await Centre(ct);
        var link = $"{(config["App:PublicUrl"] ?? "").TrimEnd('/')}/login";
        var text = $"Hi {name}, {org} has added you on ClassKendra. Sign in with this mobile number: {link}";
        return await QueueAsync(null, name, phone, MessageTypes.StaffInvite, "staff_invite", [name, org, link], text, "en", ct);
    }

    /// <summary>Puts a failed message back in the outbox. It counts against the limit again.</summary>
    public async Task<bool> RetryAsync(Message m, CancellationToken ct)
    {
        await ResetMonthIfNeeded(ct);
        if (!await CountOne(ct)) return false;
        m.Status = MessageStatus.Queued;
        m.Error = null;
        m.Attempts = 0;
        m.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Recipient(string parentName, string studentName) =>
        string.IsNullOrWhiteSpace(parentName) ? $"{studentName}'s parent" : parentName;

    /// <summary>"Thu, 5 November" like longDate() in the app.</summary>
    private static string LongDate(DateOnly d) =>
        d.ToString("ddd, d MMMM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>False when the monthly message limit is used up.</summary>
    private async Task<bool> QueueAsync(Guid? studentId, string name, string phone, string type,
        string template, string[] parameters, string text, string lang, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return false;
        if (Phone.Normalise(phone) is not { } to) return false;

        // Free plan: "_free" templates, which end with the "Powered by ClassKendra" footer.
        if (await plans.IsFreeAsync(ct))
        {
            template += "_free";
            text += $"\n\n{FreeFooter}";
        }

        await ResetMonthIfNeeded(ct);
        if (!await CountOne(ct)) return false;

        db.Messages.Add(new Message
        {
            TenantId = tenantId,
            StudentId = studentId,
            RecipientName = Cut(name, 100),
            Phone = to,
            Type = type,
            Language = lang,
            TemplateName = template,
            TemplateParams = JsonSerializer.Serialize(parameters),
            Text = Cut(text, 2000),
            Status = MessageStatus.Queued,
            Attempts = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> CountOne(CancellationToken ct) =>
        await db.Tenants
            .Where(t => t.MessagesUsed < t.MessageLimit)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.MessagesUsed, t => t.MessagesUsed + 1), ct) == 1;

    /// <summary>The count starts again on the 1st of every month.</summary>
    private Task<int> ResetMonthIfNeeded(CancellationToken ct)
    {
        var today = Clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return db.Tenants
            .Where(t => t.MessagesPeriodStart == null || t.MessagesPeriodStart < monthStart)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.MessagesUsed, 0)
                .SetProperty(t => t.MessagesPeriodStart, monthStart), ct);
    }

    private async Task<(string Name, string Lang)> Centre(CancellationToken ct)
    {
        if (_centre is null)
        {
            var t = await db.Tenants.AsNoTracking().Select(x => new { x.Name, x.MessageLanguage }).FirstAsync(ct);
            _centre = (t.Name, (t.MessageLanguage ?? "en").Trim());
        }
        return _centre.Value;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
