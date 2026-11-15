using System.Text.Json;
using Dib4t.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Http;

/// <summary>Shared plumbing of the legacy API controllers: <c>express.json()</c> bodies and use-case results.</summary>
public abstract class LegacyControllerBase : ControllerBase
{
    /// <summary>The body as <c>express.json()</c> reads it (<see cref="LegacyJsonBody"/>), or the legacy error to answer.</summary>
    protected async Task<(JsonElement Body, IActionResult? Error)> ReadJsonBodyAsync()
    {
        var (body, error) = await LegacyJsonBody.ReadAsync(Request);
        return (body, error is null ? null : new ResultAction(error));
    }

    /// <summary>200 with <paramref name="map"/> of the value, or the legacy status and text of the error.</summary>
    protected IActionResult Answer<T>(Result<T> result, Func<T, object> map)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(map);
        return result.IsSuccess ? Ok(map(result.Value)) : result.Error.ToLegacyResult();
    }

    /// <summary>Runs a minimal-API <see cref="IResult"/> from a controller action.</summary>
    private sealed class ResultAction(IResult result) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
    }
}
