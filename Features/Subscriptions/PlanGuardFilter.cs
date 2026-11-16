using ClassKendraAPI.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClassKendraAPI.Features.Subscriptions;

/// <summary>Put on controllers that must keep working after the plan ends (login, centre, payments for the plan…).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class SkipPlanCheckAttribute : Attribute;

/// <summary>
/// When a centre's trial or plan has ended (after the grace days), it can still SEE everything
/// but can't add or change records until it renews. Returns 402 with a message.
/// </summary>
public class PlanGuardFilter(CurrentUser current, PlanService plans) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        var ct = context.HttpContext.RequestAborted;
        var skipAll = current.TenantId is null || current.IsSystem
                      || context.ActionDescriptor.EndpointMetadata.OfType<SkipPlanCheckAttribute>().Any();

        // Free plan (tuition / coaching after the trial): owner login only, and up to the plan's students.
        if (!skipAll && await plans.IsFreeAsync(ct))
        {
            if (current.Role != "Owner")
            {
                context.Result = new ObjectResult(new { message = PlanService.FreeOwnerOnlyMessage, freePlan = true })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }

            // Too many students for the Free plan: read-only, but students who left can still be removed.
            var isWrite = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
            var removingStudent = HttpMethods.IsDelete(method)
                && context.HttpContext.Request.Path.StartsWithSegments("/api/v1/students");
            if (isWrite && !removingStudent && await plans.FreeLimitErrorAsync(ct) is { } limit)
            {
                context.Result = new ObjectResult(new { message = limit, freePlan = true })
                {
                    StatusCode = StatusCodes.Status402PaymentRequired
                };
                return;
            }

            await next();
            return;
        }

        var skip = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
                   || current.TenantId is null || current.IsSystem
                   || context.ActionDescriptor.EndpointMetadata.OfType<SkipPlanCheckAttribute>().Any();

        if (!skip && !await plans.IsActiveAsync(context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new { message = "Your plan has ended. Renew it to add or change records — your data is safe." })
            {
                StatusCode = StatusCodes.Status402PaymentRequired
            };
            return;
        }
        await next();
    }
}
