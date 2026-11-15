using Microsoft.JSInterop;

namespace Dib4t.Client.Auth;

/// <summary>
/// The login token in <c>localStorage["token"]</c> (PLAN.md §4.6): survives closing the tab or the browser and is
/// shared by every tab; changes made in another tab arrive through the browser's <c>storage</c> event
/// (<c>wwwroot/js/auth.js</c>). The current value is kept in memory, so the HTTP handler reads it without interop.
/// </summary>
public sealed class TokenStore(IJSRuntime js) : IAsyncDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private IJSObjectReference? _module;
    private IJSObjectReference? _watch;
    private DotNetObjectReference<TokenStore>? _self;
    private bool _initialized;

    /// <summary>Raised with the new token (<c>null</c> = logged out), for changes in this tab and in other tabs.</summary>
    public event Func<string?, Task>? Changed;

    /// <summary>The token as last read or written; <c>null</c> before <see cref="InitializeAsync"/> or when logged out.</summary>
    public string? Current { get; private set; }

    /// <summary>Reads the stored token once and starts listening to other tabs.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/auth.js");
                Current = await _module.InvokeAsync<string?>("getToken");
                _self = DotNetObjectReference.Create(this);
                _watch = await _module.InvokeAsync<IJSObjectReference>("watchToken", _self);
            }
            catch (JSException)
            {
                // No usable storage: the session lives in memory for this tab only.
            }

            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async Task SetAsync(string token)
    {
        await InitializeAsync();
        await StorageAsync("setToken", token);
        await SetCurrentAsync(token);
    }

    public async Task RemoveAsync()
    {
        await InitializeAsync();
        await StorageAsync("removeToken");
        await SetCurrentAsync(null);
    }

    /// <summary>Called by <c>auth.js</c> when another tab changed or removed the token.</summary>
    [JSInvokable]
    public Task OnTokenChanged(string? token) => SetCurrentAsync(token);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_watch is not null)
            {
                await _watch.InvokeVoidAsync("dispose");
                await _watch.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            // The page is going away.
        }

        _self?.Dispose();
        _initialization.Dispose();
    }

    /// <summary>A write to localStorage; when storage is blocked, the in-memory value still changes.</summary>
    private async Task StorageAsync(string function, params object?[] arguments)
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync(function, arguments);
        }
        catch (JSException)
        {
        }
    }

    private async Task SetCurrentAsync(string? token)
    {
        if (Current == token)
        {
            return;
        }

        Current = token;
        if (Changed is { } changed)
        {
            await changed(token);
        }
    }
}
