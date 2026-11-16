using Dib4t.Application.Logs;
using Dib4t.Contracts.Logs;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/logs</c>: the Registos tab (PLAN.md §3.3), new-app only, S. Bad filters answer
/// <c>400 "Filtro inválido: …"</c> as plain text. The request itself is logged at Debug (LoggingSetup.RequestLevel).
/// </summary>
[Route("api/logs")]
[Authorize(Policy = Policies.CanSeeDefinicoes)]
public sealed class LogsController(LogsService logs) : LegacyControllerBase
{
    /// <summary><c>?from&amp;to&amp;levels&amp;categories&amp;user&amp;requestId&amp;q&amp;cursor&amp;limit</c>.</summary>
    [HttpGet]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        var query = Request.Query;
        var input = new LogSearchInput(
            One(query["from"]), One(query["to"]), All(query["levels"]), All(query["categories"]),
            One(query["user"]), One(query["requestId"]), One(query["q"]), One(query["cursor"]), One(query["limit"]));
        return Answer(await logs.SearchAsync(input, cancellationToken), page => new LogPageResponse(
            [.. page.Items.Select(ToResponse)], page.Next?.ToString()));
    }

    [HttpGet("facets")]
    public async Task<IActionResult> Facets(CancellationToken cancellationToken)
    {
        var facets = await logs.FacetsAsync(cancellationToken);
        return Ok(new LogFacetsResponse(facets.Categories, facets.Levels, facets.Users));
    }

    private static string? One(StringValues values) => values.Count == 0 ? null : values[^1];

    private static string[] All(StringValues values) => [.. values.OfType<string>()];

    private static LogEntryResponse ToResponse(LogRecord entry) => new(
        entry.Id, entry.Timestamp, entry.Level, entry.Category, entry.SourceContext, entry.Message, entry.Exception,
        entry.PropertiesJson, entry.RequestId, entry.UserName);
}
