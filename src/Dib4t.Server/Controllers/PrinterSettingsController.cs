using Dib4t.Application.Settings;
using Dib4t.Contracts.Settings;
using Dib4t.Domain.Settings;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary><c>/api/printer-settings</c> (legacy <c>routes/printerSettings.js</c>, PLAN.md §6), every route S.</summary>
[Route("api/printer-settings")]
[Authorize(Policy = Policies.CanSeeDefinicoes)]
public sealed class PrinterSettingsController(PrinterSettingsService printers) : LegacyControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await printers.ListAsync(cancellationToken)).Select(ToResponse));

    [HttpGet("{templateName}")]
    public async Task<IActionResult> ByTemplate(string templateName, CancellationToken cancellationToken) =>
        Answer(await printers.ListByTemplateAsync(templateName, cancellationToken), list => list.Select(ToResponse));

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await printers.CreateAsync(body, cancellationToken), ToResponse);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await printers.UpdateAsync(id, body, cancellationToken), ToResponse);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken) =>
        Answer(await printers.DeleteAsync(id, cancellationToken), ToResponse);

    private static PrinterSettingsResponse ToResponse(PrinterSettings printer) =>
        new(printer.Id, printer.Ip, printer.Port, printer.TemplateName, printer.Version);
}
