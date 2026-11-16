using Microsoft.JSInterop;

namespace Dib4t.Client.Budgets;

/// <summary>Saves a file the API answered as base64 text (<c>wwwroot/js/download.js</c>, legacy <c>generatePdff</c>).</summary>
public interface IFileDownload
{
    /// <summary><c>true</c> when the browser was asked to save the file.</summary>
    Task<bool> SaveBase64Async(string fileName, string base64, string contentType);
}

/// <inheritdoc cref="IFileDownload"/>
public sealed class FileDownload(IJSRuntime js) : IFileDownload, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<bool> SaveBase64Async(string fileName, string base64, string contentType)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/download.js");
            await _module.InvokeVoidAsync("downloadBase64", fileName, base64, contentType);
            return true;
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
