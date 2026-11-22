using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>Settings → Base de Dados in one of the two UIs (legacy <c>settings/databaseBackup.jsx</c>, new <c>DatabaseTab.razor</c>).</summary>
public interface IDatabasePage
{
    /// <summary>The notifications (success and error), top right.</summary>
    ILocator Notification { get; }

    /// <summary>The confirmation dialog of a restore.</summary>
    ILocator Dialog { get; }

    /// <summary>Opens the settings page on the Base de Dados tab, with the info loaded.</summary>
    Task GotoAsync();

    /// <summary>Each statistics card as (label, value, detail) in its visible text (labels in upper case, as shown).</summary>
    Task<IReadOnlyList<(string Label, string Value, string Detail)>> StatsAsync();

    /// <summary>The collection table: the header cells, then each row's cells.</summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> TableAsync();

    /// <summary>"Transferir Backup"; returns the saved file's suggested name and bytes.</summary>
    Task<(string FileName, byte[] Content)> DownloadAsync();

    /// <summary>"Restaurar Backup" with <paramref name="path"/> chosen in the file chooser.</summary>
    Task ChooseFileAsync(string path);

    /// <summary>"Restaurar" in the confirmation dialog.</summary>
    Task ConfirmAsync();
}

/// <inheritdoc cref="IDatabasePage"/>
public sealed class NewDatabasePage(IPage page) : IDatabasePage
{
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public ILocator Dialog => page.Locator(".mud-dialog.database-dialog");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/settings");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Base de Dados" }).ClickAsync(new() { Timeout = 30_000 });
        await page.Locator(".database-table tbody tr").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task<IReadOnlyList<(string Label, string Value, string Detail)>> StatsAsync()
    {
        var cards = new List<(string, string, string)>();
        foreach (var card in await page.Locator(".database-stat").AllAsync())
        {
            var detail = card.Locator(".database-stat-detail");
            cards.Add((await card.Locator(".database-stat-label").InnerTextAsync(), await card.Locator(".database-stat-value").InnerTextAsync(),
                await detail.CountAsync() > 0 ? await detail.InnerTextAsync() : string.Empty));
        }

        return cards;
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> TableAsync() => DatabasePages.TableAsync(page.Locator(".database-table"));

    public Task<(string FileName, byte[] Content)> DownloadAsync() =>
        DatabasePages.DownloadAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Transferir Backup" }));

    public Task ChooseFileAsync(string path) => DatabasePages.ChooseFileAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Restaurar Backup" }), path);

    public Task ConfirmAsync() => Dialog.GetByRole(AriaRole.Button, new() { Name = "Restaurar", Exact = true }).ClickAsync();
}

/// <summary>What both UIs do the same way.</summary>
internal static class DatabasePages
{
    public static async Task<IReadOnlyList<IReadOnlyList<string>>> TableAsync(ILocator table)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in await table.Locator("tr").AllAsync())
        {
            rows.Add([.. (await row.Locator("th, td").AllInnerTextsAsync()).Select(t => t.Trim())]);
        }

        return rows;
    }

    public static async Task<(string FileName, byte[] Content)> DownloadAsync(IPage page, ILocator button)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => button.ClickAsync(), new() { Timeout = 60_000 });
        var path = Path.Combine(Path.GetTempPath(), $"dib4t-e2e-download-{Guid.NewGuid():N}");
        try
        {
            await download.SaveAsAsync(path);
            return (download.SuggestedFilename, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Clicks the button and answers the browser's file chooser, as a user does.</summary>
    public static async Task ChooseFileAsync(IPage page, ILocator button, string path)
    {
        var chooser = await page.RunAndWaitForFileChooserAsync(() => button.ClickAsync());
        await chooser.SetFilesAsync(path);
    }
}
