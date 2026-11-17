using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Holidays;

/// <summary>
/// The centre's holidays — one list for the whole school or coaching centre (table Holidays).
/// On a holiday: no absent alerts go to parents, automatic fee reminders wait for the next working day,
/// and (schools with staff attendance) staff are not marked absent.
/// </summary>
public class HolidayService(ClassKendraContext db)
{
    /// <summary>The holiday's name if that day is a holiday, otherwise null.</summary>
    public Task<string?> NameAsync(DateOnly day, CancellationToken ct) =>
        db.Holidays.AsNoTracking()
            .Where(h => h.FromDate <= day && h.ToDate >= day)
            .OrderBy(h => h.FromDate)
            .Select(h => h.Name)
            .FirstOrDefaultAsync(ct);

    /// <summary>Holidays that touch the range, oldest first.</summary>
    public Task<List<Holiday>> BetweenAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        db.Holidays.AsNoTracking()
            .Where(h => h.FromDate <= to && h.ToDate >= from)
            .OrderBy(h => h.FromDate).ThenBy(h => h.Name)
            .ToListAsync(ct);

    /// <summary>
    /// The holidays that ended just before <paramref name="today"/> (yesterday, the day before… while each was a holiday),
    /// oldest first. Fee reminders that fell on those days are sent today instead. Looks back at most 30 days.
    /// </summary>
    public async Task<List<DateOnly>> HolidaysJustBeforeAsync(DateOnly today, CancellationToken ct)
    {
        var holidays = await BetweenAsync(today.AddDays(-30), today.AddDays(-1), ct);
        var days = new List<DateOnly>();
        for (var d = today.AddDays(-1); d >= today.AddDays(-30); d = d.AddDays(-1))
        {
            if (!holidays.Any(h => h.FromDate <= d && d <= h.ToDate)) break;
            days.Add(d);
        }
        days.Reverse();
        return days;
    }
}
