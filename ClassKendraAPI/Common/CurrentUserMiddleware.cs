using System.Security.Claims;
using ClassKendraAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Common;

/// <summary>
/// After the token is checked, loads the user's centre and role from the database.
/// A removed (inactive) user gets 401, and Angular sends them back to login.
/// </summary>
public class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClassKendraContext db, CurrentUser current)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirstValue("sub"), out var userId))
        {
            var user = await db.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.Id == userId && u.IsActive)
                .Select(u => new { u.TenantId, u.Role })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Please sign in again." });
                return;
            }

            current.Set(userId, user.TenantId, user.Role);
            // Lets controllers use [Authorize(Roles = "Owner")]
            context.User.AddIdentity(new ClaimsIdentity([new Claim("role", user.Role)], "db", "sub", "role"));
        }

        await next(context);
    }
}