using Microsoft.AspNetCore.Mvc.Filters;

namespace Dib4t.Server.Http;

/// <summary>
/// TEMPORARY, until the auth step (PLAN.md §10 Phase 1): the endpoint needs the settings role (auth column S, §6),
/// but there are no tokens yet. Outside Production it is open (tests, E2E, local dev); in Production it answers like
/// the legacy app without a token. Replace with the <c>canSeeDefinicoes</c> policy in the auth step.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BlockedInProductionUntilAuthAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            context.Result = LegacyResults.Text(StatusCodes.Status401Unauthorized, "Access denied. No token provided.");
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
