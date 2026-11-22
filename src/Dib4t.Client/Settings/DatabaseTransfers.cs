using Dib4t.Client.Api;
using Dib4t.Client.Auth;
using Dib4t.SharedKernel.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Dib4t.Client.Settings;

/// <summary>A file chosen in the browser (it stays there: only its name, size and date come to .NET).</summary>
public sealed record ChosenFile(string Name, double Size, double LastModified);

/// <summary>
/// The answer of a backup download or restore upload: the HTTP status (<c>0</c> = no answer, or a download cut off)
/// and the body text; <see cref="Size"/> is the saved file's size.
/// </summary>
public sealed record TransferResult(int Status, string Text, double Size = 0)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// The streamed backup download and restore upload (PLAN.md §7.6 WASM caveat): done by the browser
/// (<c>wwwroot/js/database.js</c>) with the token header, progress reported here. As the HTTP handlers do for every
/// other call (§8.2), an answer that is not 2xx or 4xx shows the global error notification, and
/// <c>400 "Invalid token."</c> logs out.
/// </summary>
public interface IDatabaseTransfers
{
    /// <summary>Opens the browser's file chooser of <paramref name="input"/>.</summary>
    Task ChooseAsync(ElementReference input);

    /// <summary>The file just chosen in <paramref name="input"/> (kept in the browser for <see cref="RestoreAsync"/>), or <c>null</c>.</summary>
    Task<ChosenFile?> TakeFileAsync(ElementReference input);

    /// <summary>Forgets the chosen file (the restore was cancelled).</summary>
    Task ForgetFileAsync();

    /// <summary><c>GET /api/database/backup</c>, saved as <paramref name="fileName"/>; <paramref name="onProgress"/> gets the bytes received.</summary>
    Task<TransferResult> DownloadBackupAsync(string fileName, Func<double, Task> onProgress);

    /// <summary><c>POST /api/database/restore</c> with the chosen file; <paramref name="onProgress"/> gets (sent, total) bytes.</summary>
    Task<TransferResult> RestoreAsync(Func<double, double, Task> onProgress);

    /// <summary>Legacy <c>beforeunload</c> warning while an operation runs.</summary>
    Task GuardUnloadAsync(bool enabled);
}

/// <inheritdoc cref="IDatabaseTransfers"/>
public sealed class DatabaseTransfers(IJSRuntime js, TokenStore tokens, ISnackbar snackbar, NavigationManager navigation) : IDatabaseTransfers, IAsyncDisposable
{
    public const string Module = "./js/database.js";

    private IJSObjectReference? _module;

    public Task ChooseAsync(ElementReference input) => CallVoidAsync("chooseFile", input);

    public Task<ChosenFile?> TakeFileAsync(ElementReference input) => CallAsync<ChosenFile?>("takeFile", null, input);

    public Task ForgetFileAsync() => CallVoidAsync("forgetFile");

    public async Task<TransferResult> DownloadBackupAsync(string fileName, Func<double, Task> onProgress)
    {
        using var sink = DotNetObjectReference.Create(new ProgressSink { Download = onProgress });
        await tokens.InitializeAsync();
        return await AfterAsync(await CallAsync("downloadBackup", NoAnswer, Url("api/database/backup"), tokens.Current, fileName, sink));
    }

    public async Task<TransferResult> RestoreAsync(Func<double, double, Task> onProgress)
    {
        using var sink = DotNetObjectReference.Create(new ProgressSink { Upload = onProgress });
        await tokens.InitializeAsync();
        return await AfterAsync(await CallAsync("restoreBackup", NoAnswer, Url("api/database/restore"), tokens.Current, sink));
    }

    public Task GuardUnloadAsync(bool enabled) => CallVoidAsync("guardUnload", enabled);

    private static TransferResult NoAnswer { get; } = new(0, string.Empty);

    /// <summary>A function of the module; a script error or a page that is gone gives <paramref name="fallback"/>.</summary>
    private async Task<T> CallAsync<T>(string identifier, T fallback, params object?[] args)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", Module);
            return await _module.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            return fallback;
        }
    }

    private async Task CallVoidAsync(string identifier, params object?[] args)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", Module);
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            // nothing to do: the page is gone or the browser refused
        }
    }

    private string Url(string path) => new Uri(new Uri(navigation.BaseUri), path).ToString();

    /// <summary>What <see cref="ErrorNotificationHandler"/> and <see cref="AuthHeaderHandler"/> do for the HttpClient calls.</summary>
    private async Task<TransferResult> AfterAsync(TransferResult result)
    {
        if (ErrorNotificationHandler.IsUnexpected(result.Status))
        {
            snackbar.Add(ErrorNotificationHandler.UnexpectedError, Severity.Error);
        }
        else if (result.Status == 400 && result.Text == AuthTexts.InvalidToken && tokens.Current is not null)
        {
            await tokens.RemoveAsync();
        }

        return result;
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

    /// <summary>The progress callbacks the JS module calls.</summary>
    public sealed class ProgressSink
    {
        public Func<double, Task>? Download { get; init; }

        public Func<double, double, Task>? Upload { get; init; }

        [JSInvokable]
        public Task OnDownloadProgress(double loaded) => Download?.Invoke(loaded) ?? Task.CompletedTask;

        [JSInvokable]
        public Task OnUploadProgress(double loaded, double total) => Upload?.Invoke(loaded, total) ?? Task.CompletedTask;
    }
}
