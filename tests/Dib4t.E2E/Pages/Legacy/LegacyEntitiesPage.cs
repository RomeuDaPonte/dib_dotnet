using Dib4t.E2E.Pages.Blazor;
using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Legacy;

/// <summary>Legacy entities page (front-end/src/components/entities/*, route <c>/landing/entity</c>).</summary>
public sealed class LegacyEntitiesPage(IPage page) : IEntitiesPage
{
    public ILocator Notification => page.Locator(".Toastify__toast-body");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/entity");
        await page.Locator("table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task CreateAsync(EntityValues entity)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Add Entidade" }).ClickAsync();
        await FillAsync(entity);
    }

    public async Task EditAsync(string name, EntityValues entity)
    {
        await Filter(name);
        // The pencil's svg carries the click handler (entityModal.jsx), inside the "Editar" icon button.
        await RowOf(name).Locator("button[aria-label=Editar] svg").ClickAsync();
        await FillAsync(entity);
    }

    public async Task<IReadOnlyList<string>> RowAsync(string name)
    {
        await Filter(name);
        return await RowOf(name).Locator("td").AllInnerTextsAsync();
    }

    /// <summary>The toolbar's "filtrar" field: the first text input (its label is not linked to it: TableToolbar sets no id).</summary>
    private Task Filter(string name) => page.Locator("input:not([type=checkbox])").First.FillAsync(name);

    private ILocator RowOf(string name) =>
        page.Locator("table tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true }) });

    private async Task FillAsync(EntityValues entity)
    {
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.Locator("#name").FillAsync(entity.Name);
        await dialog.Locator("#fiscalNumber").FillAsync(entity.FiscalNumber);
        await dialog.Locator("#address").FillAsync(entity.Address);
        await dialog.Locator("#zipCode").FillAsync(entity.ZipCode);
        await dialog.Locator("#location").FillAsync(entity.Location);
        await ChooseAsync("type", entity.Type);
        await ChooseAsync("paymentCondition", entity.PaymentCondition);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();
    }

    /// <summary>A MUI v4 select: open it, pick the option, wait until its menu has faded out (it covers the dialog).</summary>
    private async Task ChooseAsync(string name, string option)
    {
        await page.Locator($"#mui-component-select-{name}").ClickAsync();
        await page.Locator($"#menu-{name}").GetByRole(AriaRole.Option, new() { Name = option, Exact = true }).ClickAsync();
        await page.Locator($"#menu-{name}").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}
