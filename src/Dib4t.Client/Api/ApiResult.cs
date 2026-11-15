using System.Net;

namespace Dib4t.Client.Api;

/// <summary>
/// An API answer for the state services: the value, or the status and the legacy plain-text body (PLAN.md §6, §8.2:
/// 4xx texts are shown to the user as they are).
/// </summary>
public sealed record ApiResult<T>(T? Value, HttpStatusCode? Status, string? ErrorText)
{
    public bool IsSuccess => ErrorText is null && Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
}

/// <summary>Factories of <see cref="ApiResult{T}"/>.</summary>
public static class ApiResult
{
    public static ApiResult<T> Success<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(value, status, null);

    public static ApiResult<T> Failure<T>(HttpStatusCode? status, string text) => new(default, status, text);
}
