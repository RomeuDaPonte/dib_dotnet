using Microsoft.AspNetCore.Diagnostics;

namespace Dib4t.Server.Http;

/// <summary>
/// D16 (PLAN.md §9, §3.2): an unhandled exception answers <c>500 "Something failed."</c> as legacy text (the intended
/// legacy behavior; the legacy app actually leaks Express's HTML stack trace). The exception and its stack trace go
/// only to the log store (category <c>App</c>); the response carries the request id in <c>x-request-id</c>.
/// </summary>
public sealed partial class LegacyExceptionHandler(ILogger<LegacyExceptionHandler> logger) : IExceptionHandler
{
    public const string Message = "Something failed.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path.Value);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = LegacyResults.TextContentType;
        await httpContext.Response.WriteAsync(Message, cancellationToken);
        return true;
    }

    /// <summary>
    /// The middleware's own fallback, required by <c>UseExceptionHandler()</c>. Not reached while
    /// <see cref="TryHandleAsync"/> handles everything; answers the same text if it ever is.
    /// </summary>
    public static Task WriteFallbackAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = LegacyResults.TextContentType;
        return httpContext.Response.WriteAsync(Message, httpContext.RequestAborted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception on {RequestMethod} {RequestPath}")]
    private partial void LogUnhandled(Exception exception, string requestMethod, string? requestPath);
}
