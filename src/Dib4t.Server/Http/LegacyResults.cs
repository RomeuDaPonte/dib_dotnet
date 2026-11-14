using Dib4t.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Http;

/// <summary>Legacy error responses: plain text as Express <c>res.status(x).send("…")</c> sends it (PLAN.md §6).</summary>
public static class LegacyResults
{
    /// <summary>Express <c>res.send(string)</c> answers with this content type.</summary>
    public const string TextContentType = "text/html; charset=utf-8";

    public static ContentResult Text(int statusCode, string message) =>
        new() { StatusCode = statusCode, Content = message, ContentType = TextContentType };

    /// <summary>Maps a use-case error to the legacy status code and message (§3.2).</summary>
    public static ContentResult ToLegacyResult(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Text(error.Kind switch
        {
            ErrorKind.Validation or ErrorKind.BusinessRule => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        }, error.Message);
    }
}
