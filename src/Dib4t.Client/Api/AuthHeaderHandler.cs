using System.Net;
using Dib4t.Client.Auth;
using Dib4t.SharedKernel.Auth;

namespace Dib4t.Client.Api;

/// <summary>
/// Every API request carries the token in <c>x-auth-token</c> (legacy <c>http.setJwt</c>). An answer
/// <c>400 "Invalid token."</c> (e.g. after a key change, or the user was deleted) removes the stored token, so the app
/// shows the login page instead of errors (PLAN.md §4.6). Nothing else logs the user out: a 5xx or a network error
/// (server down) keeps the session.
/// </summary>
public sealed class AuthHeaderHandler(TokenStore tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await tokens.InitializeAsync();
        if (tokens.Current is { } token)
        {
            request.Headers.Remove(AuthTexts.TokenHeader);
            request.Headers.TryAddWithoutValidation(AuthTexts.TokenHeader, token);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest && tokens.Current is not null)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            if (await response.Content.ReadAsStringAsync(cancellationToken) == AuthTexts.InvalidToken)
            {
                await tokens.RemoveAsync();
            }
        }

        return response;
    }
}
