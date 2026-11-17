using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using ClassKendraAPI.Features.Activity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Holidays;

// Matches Holiday and HolidayInput in models.ts.
public record HolidayView(string Id, string Name, string From, string To, int Days);
public record HolidaySaveInput(string? Name, string? From, string? To);

/// <summary>
/// Holidays for the whole centre — every school and coaching centre, on every plan.
/// (Schools with staff attendance see the same list under Staff attendance › Settings.)
/// </summary>
[ApiController]
[Route("api/v1/holidays")]
[Authorize]
public class HolidaysController(ClassKendraContext db, CurrentUser current, HolidayService holidays, AuditService audit) : ControllerBase
{
    /// <summary>
    /// GET /holidays?from=&to= — everyone signed in. Default: from 30 days ago to a year ahead, oldest first.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<HolidayView>>> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var today = Clock.Today;
        var start = from ?? today.AddDays(-30);
        var end = to ?? today.AddYears(1);
        if (end < start) return BadRequest(new { message = "The end date is before the start date." });
        return (await holidays.BetweenAsync(start, end, ct)).Select(ToView).ToList();
    }

    /// <summary>GET /holidays/on?date= → { holiday: "Diwali" } or { holiday: null }. Default: today.</summary>
    [HttpGet("on")]
    public async Task<IActionResult> On([FromQuery] DateOnly? date, CancellationToken ct) =>
        Ok(new { holiday = await holidays.NameAsync(date ?? Clock.Today, ct) });

    /// <summary>POST /holidays { name, from, to } — owner. One day (to empty) or a range, at most 3 months.</summary>
    [HttpPost]
    [Authorize(Roles = "Owner")]
    public async Task<ActionResult<HolidayView>> Add(HolidaySaveInput input, CancellationToken ct)
    {
        if (current.TenantId is not Guid tenantId) return BadRequest(new { message = "Set up your centre first." });
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 60) return BadRequest(new { message = "Give the holiday a name (up to 60 letters)." });
        if (!DateOnly.TryParse(input.From, out var from)) return BadRequest(new { message = "Choose the date." });
        var to = DateOnly.TryParse(input.To, out var t) ? t : from;
        if (to < from) return BadRequest(new { message = "The last day can't be before the first day." });
        if (to.DayNumber - from.DayNumber > 92) return BadRequest(new { message = "A holiday can be at most 3 months long." });

        var h = new Holiday { TenantId = tenantId, Name = name, FromDate = from, ToDate = to };
        db.Holidays.Add(h);
        await audit.AddAsync(AuditActions.HolidayAdded, "Holiday", null, $"Added holiday {name} ({Range(from, to)})", null, null, ct);
        await db.SaveChangesAsync(ct);
        return ToView(h);
    }

    /// <summary>DELETE /holidays/{id} — owner.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var h = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return NoContent();
        db.Holidays.Remove(h);
        await audit.AddAsync(AuditActions.HolidayRemoved, "Holiday", h.Id, $"Removed holiday {h.Name} ({Range(h.FromDate, h.ToDate)})", null, null, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string Range(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("d MMM yyyy", En) : $"{from.ToString("d MMM", En)} – {to.ToString("d MMM yyyy", En)}";

    private static readonly System.Globalization.CultureInfo En = System.Globalization.CultureInfo.InvariantCulture;

    private static HolidayView ToView(Holiday h) => new(
        h.Id.ToString(), h.Name, h.FromDate.ToString("yyyy-MM-dd"), h.ToDate.ToString("yyyy-MM-dd"),
        h.ToDate.DayNumber - h.FromDate.DayNumber + 1);
}
