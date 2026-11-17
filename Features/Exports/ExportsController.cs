using System.IO.Compression;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Activity;
using ClassKendraAPI.Features.Fees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Exports;

/// <summary>
/// Excel downloads (Settings › Download to Excel). Every download is written to the activity log.
///   GET /exports/students?status=All|Active|Left
///   GET /exports/fees?from=&to=        bills raised for those months
///   GET /exports/receipts?from=&to=    receipts, cancelled ones marked
///   GET /exports/refunds?from=&to=
///   GET /exports/daybook?from=&to=     money in and out, with daily totals by mode
///   GET /exports/all                   owner only: a .zip with every Excel file — all of the centre's data
/// Dates default to the start of this session up to today; a range can be at most 400 days.
/// </summary>
[ApiController]
[Route("api/v1/exports")]
[Authorize(Roles = "Owner,Staff")]
public class ExportsController(ClassKendraContext db, ExportService exports, AuditService audit) : ControllerBase
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] Kinds = ["students", "fees", "receipts", "refunds", "daybook"];

    [HttpGet("{kind}")]
    public async Task<IActionResult> One(string kind, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? status, CancellationToken ct)
    {
        kind = kind.ToLowerInvariant();
        if (kind == "all") return await All(ct);
        if (!Kinds.Contains(kind)) return NotFound(new { message = "Unknown download." });

        var (start, end, error) = await Range(from, to, ct);
        if (error is not null) return BadRequest(new { message = error });

        var x = new XlsxWriter();
        string name, what;
        switch (kind)
        {
            case "students":
                await exports.StudentsAsync(x, status, ct);
                name = $"students-{Clock.Today:yyyy-MM-dd}"; what = "students";
                break;
            case "fees":
                await exports.FeesAsync(x, start, end, ct);
                name = $"fees-{start:yyyy-MM}-to-{end:yyyy-MM}"; what = $"fees {start:MMM yyyy} – {end:MMM yyyy}";
                break;
            case "receipts":
                await exports.ReceiptsAsync(x, start, end, includeOpening: false, ct);
                name = $"receipts-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}"; what = $"receipts {start:dd/MM/yyyy} – {end:dd/MM/yyyy}";
                break;
            case "refunds":
                await exports.RefundsAsync(x, start, end, ct);
                name = $"refunds-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}"; what = $"refunds {start:dd/MM/yyyy} – {end:dd/MM/yyyy}";
                break;
            default:
                await exports.DayBookAsync(x, start, end, ct);
                name = $"day-book-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}"; what = $"day-book {start:dd/MM/yyyy} – {end:dd/MM/yyyy}";
                break;
        }

        await audit.AddAsync(AuditActions.DataExported, "Settings", null, $"Downloaded {what} (Excel)", null, null, ct);
        await db.SaveChangesAsync(ct);
        return File(x.ToBytes(), Xlsx, $"classkendra-{name}.xlsx");
    }

    /// <summary>
    /// GET /exports/all — owner only. Everything the centre has in ClassKendra, as Excel files in one .zip:
    /// students, fees, receipts, refunds and fee changes, day-book, attendance, staff and staff attendance,
    /// setup (centre, classes/batches, fee setup), holidays, WhatsApp messages and the activity log.
    /// </summary>
    [HttpGet("all")]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> All(CancellationToken ct)
    {
        if (!User.IsInRole("Owner")) return Forbid();
        var since = new DateOnly(2000, 1, 1);
        var today = Clock.Today;
        var centre = await db.Tenants.Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "centre";

        var files = new List<(string Name, XlsxWriter X)>();
        async Task Add(string file, Func<XlsxWriter, Task> build)
        {
            var x = new XlsxWriter();
            await build(x);
            files.Add((file, x));
        }

        await Add("1-students.xlsx", x => exports.StudentsAsync(x, null, ct));
        await Add("2-fees.xlsx", async x => { await exports.FeesAsync(x, since, today.AddYears(2), ct); await exports.AdjustmentsAsync(x, ct); });
        await Add("3-receipts-and-refunds.xlsx", async x => { await exports.ReceiptsAsync(x, since, today, includeOpening: true, ct); await exports.RefundsAsync(x, since, today, ct); });
        await Add("4-day-book.xlsx", x => exports.DayBookAsync(x, since, today, ct));
        await Add("5-attendance.xlsx", x => exports.AttendanceAsync(x, since, today, ct));
        await Add("6-staff.xlsx", async x => { await exports.StaffAsync(x, ct); await exports.StaffAttendanceAsync(x, since, today, ct); });
        await Add("7-setup.xlsx", async x => { await exports.SetupAsync(x, ct); await exports.HolidaysAsync(x, ct); });
        await Add("8-whatsapp-messages.xlsx", x => exports.MessagesAsync(x, since, ct));
        await Add("9-activity-log.xlsx", x => exports.ActivityAsync(x, ct));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (file, x) in files)
            {
                var entry = zip.CreateEntry(file, CompressionLevel.Fastest);   // .xlsx is already compressed
                await using var s = entry.Open();
                x.WriteTo(s);
            }
            var readme = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
            await using (var w = new StreamWriter(readme.Open()))
                await w.WriteAsync($"""
                    All of {centre}'s data in ClassKendra, downloaded {today:dd-MM-yyyy}.
                    Each file opens in Excel, Google Sheets or LibreOffice. Money is in rupees; dates and times are India time.

                    1-students            every student, with what they owe and any advance
                    2-fees                every fee raised (removed ones marked), and every waiver or extra charge
                    3-receipts-and-refunds every receipt (cancelled ones marked) and every refund
                    4-day-book            money in and out by day and payment mode
                    5-attendance          every attendance register, with who was absent
                    6-staff               staff and teachers, staff attendance and leave
                    7-setup               centre details, classes/batches, fee setup, holidays
                    8-whatsapp-messages   every WhatsApp message sent to parents and staff
                    9-activity-log        cancelled payments, waivers, refunds, students who left… with who and why
                    """);
        }

        await audit.AddAsync(AuditActions.DataExported, "Settings", null, "Downloaded all of the centre's data (zip of Excel files)", null, null, ct);
        await db.SaveChangesAsync(ct);
        var safe = new string(centre.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return File(ms.ToArray(), "application/zip", $"classkendra-{(safe.Length > 0 ? safe : "data")}-{today:yyyy-MM-dd}.zip");
    }

    /// <summary>from/to, defaulting to this session's start and today; at most 400 days.</summary>
    private async Task<(DateOnly, DateOnly, string?)> Range(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = Clock.Today;
        var sessionStart = await db.FeeSettings.Select(f => (int?)f.SessionStartMonth).FirstOrDefaultAsync(ct) ?? 4;
        var defaultFrom = new DateOnly(today.Month >= sessionStart ? today.Year : today.Year - 1, sessionStart, 1);
        var start = from ?? defaultFrom;
        var end = to ?? today;
        if (end < start) return (start, end, "The end date is before the start date.");
        if (end.DayNumber - start.DayNumber > 400) return (start, end, "Choose a range of 400 days or less.");
        return (start, end, null);
    }
}
