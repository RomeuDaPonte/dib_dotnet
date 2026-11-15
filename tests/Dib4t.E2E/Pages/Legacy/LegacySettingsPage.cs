using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy settings page (front-end/src/components/settings/*, route <c>/landing/settings</c>).</summary>
public sealed class LegacySettingsPage(IPage page) : ISettingsPage
{
    public ILocator Notification => page.Locator(".Toastify__toast-body");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/settings");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add User" }).WaitForAsync(new() { Timeout = 30_000 });
    }

    /// <summary>
    /// Clicks the tab and waits for react-swipeable-views to finish its slide: focusing a field while it moves scrolls
    /// the slide container, and the component then jumps to the next tab.
    /// </summary>
    public async Task OpenTabAsync(string tab)
    {
        await page.GetByRole(AriaRole.Tab, new() { Name = tab }).ClickAsync();
        await page.WaitForTimeoutAsync(600);
    }

    public async Task CreateUserAsync(string name, string email, string password, params string[] roleLabels)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Add User" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("#name").FillAsync(name);
        await dialog.Locator("#email").FillAsync(email);
        await dialog.Locator("#password").FillAsync(password);
        await dialog.Locator("#confirmPassword").FillAsync(password);
        foreach (var label in roleLabels)
        {
            await dialog.GetByLabel(label, new() { Exact = true }).CheckAsync();
        }

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();
    }

    public async Task SetAutomationPriceAsync(string text)
    {
        await OpenTabAsync("Preços");
        // The pencil's svg carries the click handler (pricesModal.jsx), inside the "Editar" icon button.
        await page.Locator("table button[aria-label=Editar] svg").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("#automation").FillAsync(text);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Atualizar" }).ClickAsync();
    }

    public Task<string> AutomationPriceAsync() => page.Locator("table tbody td").Nth(1).InnerTextAsync();

    public async Task AddPaymentMethodAsync(string name)
    {
        await OpenTabAsync("M/Pagamento");
        await page.Locator("#Novo").FillAsync(name);
        await page.Locator("#Novo").PressAsync("Enter");
        await page.GetByText(name, new() { Exact = true }).WaitForAsync();
    }

    public async Task AddPrinterAsync(string ip, string port, string templateLabel)
    {
        await OpenTabAsync("Impressoras");
        await page.Locator("input[name=ip]").FillAsync(ip);
        await page.Locator("input[name=port]").FillAsync(port);
        await page.Locator("#mui-component-select-templateName, [aria-haspopup=listbox]").Last.ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = templateLabel }).ClickAsync();
        await page.Locator("#menu-templateName").WaitForAsync(new() { State = WaitForSelectorState.Detached }); // the menu fades out over the button
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar" }).ClickAsync();
    }
}
