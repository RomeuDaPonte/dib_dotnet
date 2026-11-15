using Dib4t.Application.Settings;
using Dib4t.Contracts.Settings;
using Dib4t.Domain.Settings;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary><c>/api/prices</c> (legacy <c>routes/prices.js</c>, PLAN.md §6): read by every signed-in user, changed with <c>canSeeDefinicoes</c>.</summary>
[Route("api/prices")]
public sealed class PricesController(PricesService prices) : LegacyControllerBase
{
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var current = await prices.GetAsync(cancellationToken);
        return current is null ? Ok(new { }) : Ok(ToResponse(current));
    }

    [HttpPost]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Update(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await prices.UpdateAsync(body, cancellationToken), ToResponse);
    }

    private static PricesResponse ToResponse(Prices prices) =>
        new(prices.Automation, prices.Consultancy, prices.Development, prices.Machining, prices.Assembly, prices.Margin);
}
