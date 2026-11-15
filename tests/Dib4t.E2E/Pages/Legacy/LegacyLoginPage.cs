using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy login page (front-end/src/components/account/login.jsx, route <c>/</c>).</summary>
public sealed class LegacyLoginPage(IPage page)
{
    public ILocator Email => page.Locator("input[name=email]");

    public ILocator Password => page.Locator("input[name=password]");

    public ILocator Submit => page.GetByRole(AriaRole.Button, new() { Name = "Submit" });

    /// <summary>react-toastify notifications (top right).</summary>
    public ILocator Notification => page.Locator(".Toastify__toast-body");

    public async Task SubmitAsync(string email, string password)
    {
        await Email.FillAsync(email);
        await Password.FillAsync(password);
        await Submit.ClickAsync();
    }

    public async Task GotoAsync()
    {
        // The legacy front-end derives its API URL from the page origin (src/_constants/url.js), so it must be
        // opened on http://localhost:3000 (API on :3001).
        await page.GotoAsync("/");
        await Email.WaitForAsync();
    }

    /// <summary>Signs in and waits for the landing shell.</summary>
    public async Task<LegacyShell> LoginAsync(string email, string password)
    {
        await Email.FillAsync(email);
        await Password.FillAsync(password);
        await Submit.ClickAsync();
        await page.WaitForURLAsync("**/landing/home");
        var shell = new LegacyShell(page);
        await shell.UserName.WaitForAsync();
        return shell;
    }
}
