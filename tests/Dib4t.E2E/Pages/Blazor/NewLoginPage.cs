using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New login page (Dib4t.Client Pages/Login.razor, route <c>/</c>).</summary>
public sealed class NewLoginPage(IPage page)
{
    public ILocator Heading => page.Locator("main h1");

    public ILocator Email => page.Locator("#email");

    public ILocator Password => page.Locator("#password");

    public ILocator Submit => page.GetByRole(AriaRole.Button, new() { Name = "Submit" });

    /// <summary>Error notifications (MudBlazor snackbar, top right).</summary>
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    /// <summary>Opens <c>/</c> and waits for the WebAssembly client to show the form.</summary>
    public async Task GotoAsync()
    {
        await page.GotoAsync("/");
        await Email.WaitForAsync(new() { Timeout = 30_000 }); // first start downloads the runtime
    }

    public async Task SubmitAsync(string email, string password)
    {
        await Email.FillAsync(email);
        await Password.FillAsync(password);
        await Submit.ClickAsync();
    }

    /// <summary>Signs in and waits for the landing shell.</summary>
    public async Task<NewShell> LoginAsync(string email, string password)
    {
        await SubmitAsync(email, password);
        await page.WaitForURLAsync("**/landing/home");
        var shell = new NewShell(page);
        await shell.UserName.WaitForAsync();
        return shell;
    }
}
