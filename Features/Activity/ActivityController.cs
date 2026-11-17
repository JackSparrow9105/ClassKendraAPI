using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Activity;

// Matches ActivityEntry in models.ts.
public record ActivityDto(
    string Id, string At, string UserName, string UserRole, string Action,
    string EntityType, string? EntityId, string Summary, string? Reason);

[ApiController]
[Route("api/v1/activity")]
[Authorize(Roles = "Owner")]
public class ActivityController(ClassKendraContext db) : ControllerBase
{
    /// <summary>GET /activity?action=&search=&page=&pageSize= — newest first. Owner only.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<ActivityDto>>> List([FromQuery] string? action, [FromQuery] string? search,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var (p, size, skip) = Paging.Clean(page, pageSize);

        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(a => a.Summary.Contains(term) || a.UserName.Contains(term) || (a.Reason != null && a.Reason.Contains(term)));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip(skip).Take(size).ToListAsync(ct);

        return new Paged<ActivityDto>(
            rows.Select(a => new ActivityDto(
                a.Id.ToString(), DateTime.SpecifyKind(a.At, DateTimeKind.Utc).ToString("O"),
                a.UserName, a.UserRole, a.Action, a.EntityType, a.EntityId?.ToString(), a.Summary, a.Reason)).ToList(),
            total, p, size);
    }
}
