using Dib4t.Server.Http;
using Dib4t.SharedKernel.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Dib4t.Server.Auth;

/// <summary>
/// The legacy auth responses (PLAN.md §4.6; <c>middleware/auth.js</c>, <c>authCanSeeSettings.js</c>), plain text and
/// without a <c>WWW-Authenticate</c> header: no or empty token → 401 <see cref="AuthTexts.NoToken"/>; a token that is
/// not valid or whose user no longer exists → 400 <see cref="AuthTexts.InvalidToken"/>; a valid token without the
/// role → 401 <see cref="AuthTexts.NoPermission"/>. Rejections are logged in category <c>Auth</c>.
/// </summary>
public sealed partial class LegacyAuthorizationResultHandler(ILogger<LegacyAuthorizationResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Challenged)
        {
            if (string.IsNullOrEmpty(context.Request.Headers[AuthTexts.TokenHeader]))
            {
                await WriteAsync(context, StatusCodes.Status401Unauthorized, AuthTexts.NoToken);
            }
            else
            {
                LogInvalidToken(context.Request.Method, context.Request.Path.Value);
                await WriteAsync(context, StatusCodes.Status400BadRequest, AuthTexts.InvalidToken);
            }

            return;
        }

        if (authorizeResult.Forbidden)
        {
            LogPermissionDenied(context.User.Identity?.Name, context.Request.Method, context.Request.Path.Value);
            await WriteAsync(context, StatusCodes.Status401Unauthorized, AuthTexts.NoPermission);
            return;
        }

        await next(context);
    }

    private static Task WriteAsync(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = LegacyResults.TextContentType;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Invalid token on {RequestMethod} {RequestPath}")]
    private partial void LogInvalidToken(string requestMethod, string? requestPath);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Permission denied for {User} on {RequestMethod} {RequestPath}")]
    private partial void LogPermissionDenied(string? user, string requestMethod, string? requestPath);
}
