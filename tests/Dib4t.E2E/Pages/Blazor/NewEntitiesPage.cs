using Microsoft.Playwright;

namespace Dib4t.E2E.Pages.Blazor;

/// <summary>New entities page (Dib4t.Client Pages/Landing/Entities.razor, Components/Entities/*).</summary>
public sealed class NewEntitiesPage(IPage page) : IEntitiesPage
{
    public ILocator Notification => page.Locator(".mud-snackbar-content-message");

    public async Task GotoAsync()
    {
        await page.GotoAsync("/landing/entity");
        await page.Locator(".entities-table tbody tr td").First.WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task CreateAsync(EntityValues entity)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Add Entidade" }).ClickAsync();
        await FillAsync(entity);
    }

    public async Task EditAsync(string name, EntityValues entity)
    {
        await page.Locator("#entities-filter").FillAsync(name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Editar " + name, Exact = true }).ClickAsync();
        await FillAsync(entity);
    }

    public async Task<IReadOnlyList<string>> RowAsync(string name)
    {
        await page.Locator("#entities-filter").FillAsync(name);
        return await page.Locator(".entities-table tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true }) })
            .Locator("td").AllInnerTextsAsync();
    }

    private async Task FillAsync(EntityValues entity)
    {
        await page.Locator("#entity-name").FillAsync(entity.Name);
        await page.Locator("#entity-fiscal-number").FillAsync(entity.FiscalNumber);
        await page.Locator("#entity-address").FillAsync(entity.Address);
        await page.Locator("#entity-zip-code").FillAsync(entity.ZipCode);
        await page.Locator("#entity-location").FillAsync(entity.Location);
        await page.Locator("#entity-type").SelectOptionAsync(new SelectOptionValue { Label = entity.Type });
        await page.Locator("#entity-payment-condition").SelectOptionAsync(new SelectOptionValue { Label = entity.PaymentCondition });
        await page.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();
    }
}

/// <summary>What the entity scenarios do, in either UI.</summary>
public interface IEntitiesPage
{
    /// <summary>The notifications (success and error), top right.</summary>
    ILocator Notification { get; }

    Task GotoAsync();

    /// <summary>"Add Entidade", every field, "Concluir".</summary>
    Task CreateAsync(EntityValues entity);

    /// <summary>Filters by <paramref name="name"/>, opens its edit dialog, replaces every field, "Concluir".</summary>
    Task EditAsync(string name, EntityValues entity);

    /// <summary>The cells (name, location, type) of the row of <paramref name="name"/>, after filtering by it.</summary>
    Task<IReadOnlyList<string>> RowAsync(string name);
}

/// <summary>The values typed and chosen in the entity dialog.</summary>
public sealed record EntityValues(string Name, string FiscalNumber, string Address, string ZipCode, string Location, string Type, string PaymentCondition);
