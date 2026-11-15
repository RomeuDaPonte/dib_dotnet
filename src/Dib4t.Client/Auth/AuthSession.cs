using System.Security.Claims;
using Dib4t.Client.Api;
using Microsoft.AspNetCore.Components.Authorization;

namespace Dib4t.Client.Auth;

/// <summary>
/// The login state of the app (PLAN.md §4.6, §8.2). Logged in = a token in <see cref="TokenStore"/>; there is no
/// expiry, only <see cref="LogoutAsync"/> ends it (in every tab). On start the user's name and roles are refreshed
/// from <c>GET /api/account/me</c>, so a changed role shows without a new login. A <c>400 "Invalid token."</c> from
/// any request removes the token (<see cref="AuthHeaderHandler"/>), which logs the user out here too.
/// </summary>
public sealed class AuthSession : AuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState LoggedOut = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly TokenStore _tokens;
    private readonly IAccountApi _account;
    private Task<AuthenticationState>? _start;
    private SessionUser? _user;

    public AuthSession(TokenStore tokens, IAccountApi account)
    {
        _tokens = tokens;
        _account = account;
        _tokens.Changed += OnTokenChangedAsync;
    }

    /// <summary>The signed-in user, or <c>null</c>.</summary>
    public SessionUser? User => _user;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _start ??= StartAsync();

    /// <summary>Signs in; <c>null</c> on success, else the server's text for the error notification.</summary>
    public async Task<string?> LoginAsync(string email, string password)
    {
        var result = await _account.LoginAsync(email, password);
        if (!result.IsSuccess)
        {
            return result.ErrorText;
        }

        await _tokens.SetAsync(result.Value!);
        return null;
    }

    /// <summary>The logout button: removes the token in this tab and, through the storage event, in every other.</summary>
    public Task LogoutAsync() => _tokens.RemoveAsync();

    public void Dispose() => _tokens.Changed -= OnTokenChangedAsync;

    private async Task<AuthenticationState> StartAsync()
    {
        await _tokens.InitializeAsync();
        _user = SessionUser.FromToken(_tokens.Current);
        if (_user is null)
        {
            if (_tokens.Current is not null)
            {
                await _tokens.RemoveAsync(); // not a token this app can read (legacy getCurrentUser → null)
            }

            return LoggedOut;
        }

        await RefreshAsync();
        return State();
    }

    /// <summary>Name and roles as stored now. On a network error the token's claims stay in use.</summary>
    private async Task RefreshAsync()
    {
        var me = await _account.GetMeAsync();
        if (me.IsSuccess && _tokens.Current is not null)
        {
            _user = me.Value;
        }
    }

    private async Task OnTokenChangedAsync(string? token)
    {
        _user = SessionUser.FromToken(token);
        if (_user is not null)
        {
            await RefreshAsync();
        }

        var state = State();
        _start = Task.FromResult(state);
        NotifyAuthenticationStateChanged(_start);
    }

    private AuthenticationState State() => _user is null ? LoggedOut : new AuthenticationState(_user.ToPrincipal());
}
