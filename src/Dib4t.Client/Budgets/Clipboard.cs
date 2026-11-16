using Microsoft.JSInterop;

namespace Dib4t.Client.Budgets;

/// <summary>Copies a text to the clipboard (<c>wwwroot/js/clipboard.js</c>, legacy <c>fallbackCopyToClipboard.js</c>).</summary>
public interface IClipboard
{
    /// <summary><c>true</c> when the text was copied.</summary>
    Task<bool> CopyAsync(string text);
}

/// <inheritdoc cref="IClipboard"/>
public sealed class Clipboard(IJSRuntime js) : IClipboard, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<bool> CopyAsync(string text)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            return await _module.InvokeAsync<bool>("copyText", text);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // the page is gone
            }
        }
    }
}
