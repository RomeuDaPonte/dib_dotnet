using MudBlazor;

namespace Dib4t.Client.Api;

/// <summary>
/// Global HTTP error handling (legacy <c>httpService.js</c> axios interceptor, PLAN.md §8.2): every answer that is not
/// an "expected" error (4xx) or a success, and every network error, shows the error notification
/// <see cref="UnexpectedError"/> once, for any request. 4xx answers are left to the caller, which shows the server's
/// text where the legacy page does. The answer itself is passed on unchanged (a network error is rethrown).
/// </summary>
public sealed class ErrorNotificationHandler(ISnackbar snackbar) : DelegatingHandler
{
    /// <summary>The legacy text, typo included.</summary>
    public const string UnexpectedError = "An anexpected error occurred";

    /// <summary>Legacy <c>espectedError</c>: 400–499.</summary>
    public static bool IsExpectedError(int status) => status is >= 400 and < 500;

    /// <summary>What the interceptor rejects without being an expected error: no answer, or not 2xx and not 4xx.</summary>
    public static bool IsUnexpected(int? status) => status is not (>= 200 and < 300) && !(status is { } s && IsExpectedError(s));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            Notify();
            throw;
        }

        if (IsUnexpected((int)response.StatusCode))
        {
            Notify();
        }

        return response;
    }

    private void Notify() => snackbar.Add(UnexpectedError, Severity.Error);
}
