using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>
/// Any page of the new Blazor WebAssembly app. Prerendering is off (PLAN.md §2), so content appears only after the
/// WebAssembly runtime started; <see cref="GotoAsync"/> waits for that. Feature page objects are added per slice.
/// </summary>
public sealed class NewAppPage(IPage page)
{
    public ILocator Heading => page.Locator("main h1");

    /// <summary>Opens <paramref name="path"/> and waits until the client rendered its first heading.</summary>
    public async Task<IResponse?> GotoAsync(string path)
    {
        var response = await page.GotoAsync(path);
        await Heading.First.WaitForAsync(new() { Timeout = 30_000 }); // first WASM start downloads the runtime
        return response;
    }
}
