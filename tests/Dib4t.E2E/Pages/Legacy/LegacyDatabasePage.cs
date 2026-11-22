using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy Settings → Base de Dados (<c>front-end/src/components/settings/databaseBackup.jsx</c>; makeStyles class names of the dev build).</summary>
public sealed class LegacyDatabasePage(IPage page) : IDatabasePage
{
    private ILocator Panel => page.Locator("[role=tabpanel]:not([hidden])");

    public ILocator Notification => page.Locator(".Toastify__toast-body");

    public ILocator Dialog => page.GetByRole(AriaRole.Dialog);

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/settings");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Base de Dados" }).ClickAsync(new() { Timeout = 30_000 });
        await Panel.Locator("table tbody tr").First.WaitForAsync(new() { Timeout = 30_000 });
        await page.WaitForTimeoutAsync(600); // react-swipeable-views' slide
    }

    public async Task<IReadOnlyList<(string Label, string Value, string Detail)>> StatsAsync()
    {
        var cards = new List<(string, string, string)>();
        foreach (var card in await Panel.Locator("[class*=makeStyles-statCard]").AllAsync())
        {
            var detail = card.Locator("[class*=makeStyles-statDetail]");
            cards.Add((await card.Locator("[class*=makeStyles-statLabel]").InnerTextAsync(), await card.Locator("[class*=makeStyles-statValue]").InnerTextAsync(),
                await detail.CountAsync() > 0 ? await detail.InnerTextAsync() : string.Empty));
        }

        return cards;
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> TableAsync() => DatabasePages.TableAsync(Panel.Locator("table"));

    public Task<(string FileName, byte[] Content)> DownloadAsync() =>
        DatabasePages.DownloadAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Transferir Backup" }));

    public Task ChooseFileAsync(string path) => DatabasePages.ChooseFileAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Restaurar Backup" }), path);

    public Task ConfirmAsync() => Dialog.GetByRole(AriaRole.Button, new() { Name = "Restaurar", Exact = true }).ClickAsync();
}
