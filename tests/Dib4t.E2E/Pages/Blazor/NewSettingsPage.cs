using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New settings page (Dib4t.Client Pages/Landing/Settings.razor and Components/Settings/*).</summary>
public sealed class NewSettingsPage(IPage page) : ISettingsPage
{
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/settings");
        await page.Locator(".settings-table tbody tr").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public Task OpenTabAsync(string tab) => page.GetByRole(AriaRole.Tab, new() { Name = tab }).ClickAsync();

    public async Task CreateUserAsync(string name, string email, string password, params string[] roleLabels)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Add User" }).ClickAsync();
        await page.Locator("#user-name").FillAsync(name);
        await page.Locator("#user-email").FillAsync(email);
        await page.Locator("#user-password").FillAsync(password);
        await page.Locator("#user-confirm-password").FillAsync(password);
        foreach (var label in roleLabels)
        {
            await page.Locator(".settings-roles").GetByText(label, new() { Exact = true }).ClickAsync();
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();
    }

    public async Task SetAutomationPriceAsync(string text)
    {
        await OpenTabAsync("Preços");
        await page.GetByRole(AriaRole.Button, new() { Name = "Editar preços" }).ClickAsync();
        await page.Locator("#prices-automation").FillAsync(text);
        await page.GetByRole(AriaRole.Button, new() { Name = "Atualizar" }).ClickAsync();
    }

    public Task<string> AutomationPriceAsync() => page.Locator(".settings-prices tbody td").Nth(1).InnerTextAsync();

    public async Task AddPaymentMethodAsync(string name)
    {
        await OpenTabAsync("M/Pagamento");
        await page.Locator("#payment-method-new").FillAsync(name);
        await page.Locator("#payment-method-new").PressAsync("Enter");
        await page.Locator(".settings-list").GetByText(name, new() { Exact = true }).WaitForAsync();
    }

    public async Task AddPrinterAsync(string ip, string port, string templateLabel)
    {
        await OpenTabAsync("Impressoras");
        await page.Locator("#printer-ip").FillAsync(ip);
        await page.Locator("#printer-port").FillAsync(port);
        await page.GetByLabel("Template").SelectOptionAsync(new SelectOptionValue { Label = templateLabel });
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar" }).ClickAsync();
    }
}

/// <summary>What the settings scenarios do, in either UI.</summary>
public interface ISettingsPage
{
    /// <summary>The notifications (success and error), top right.</summary>
    ILocator Notification { get; }

    Task GotoAsync();

    Task CreateUserAsync(string name, string email, string password, params string[] roleLabels);

    Task SetAutomationPriceAsync(string text);

    /// <summary>The automation price cell of the prices table, e.g. <c>32.5 €</c>.</summary>
    Task<string> AutomationPriceAsync();

    Task AddPaymentMethodAsync(string name);

    Task AddPrinterAsync(string ip, string port, string templateLabel);
}
