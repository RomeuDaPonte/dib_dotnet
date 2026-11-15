using Dib4t.Client.Auth;

namespace Dib4t.Client.Api;

/// <summary><c>/api/account</c> (PLAN.md §6, §8.2).</summary>
public interface IAccountApi
{
    /// <summary><c>POST /api/account/login</c>: the raw JWT, or the legacy 400 text.</summary>
    Task<ApiResult<string>> LoginAsync(string email, string password);

    /// <summary><c>GET /api/account/me</c>: the signed-in user as stored now.</summary>
    Task<ApiResult<SessionUser>> GetMeAsync();
}
