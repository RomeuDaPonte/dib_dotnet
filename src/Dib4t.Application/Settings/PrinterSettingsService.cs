using System.Text.Json;
using Dib4t.Application.Common;
using Dib4t.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Settings;

/// <summary>Legacy <c>routes/printerSettings.js</c> (every route S), PLAN.md §6, §7.2. Changes are logged in category <c>Printer</c>.</summary>
public sealed partial class PrinterSettingsService(IPrinterSettingsRepository printers, ILogger<PrinterSettingsService> logger)
{
    public Task<IReadOnlyList<PrinterSettings>> ListAsync(CancellationToken cancellationToken) => printers.ListAsync(cancellationToken);

    /// <summary><c>GET /:templateName</c>: 404 <see cref="SettingsMessages.TemplateNotFound"/> when there is none.</summary>
    public async Task<Result<IReadOnlyList<PrinterSettings>>> ListByTemplateAsync(string templateName, CancellationToken cancellationToken)
    {
        var list = await printers.ListByTemplateAsync(templateName, cancellationToken);
        return list.Count == 0 ? Error.NotFound(SettingsMessages.TemplateNotFound) : Result<IReadOnlyList<PrinterSettings>>.Success(list);
    }

    /// <summary><c>POST /</c>: Joi (400) → insert.</summary>
    public async Task<Result<PrinterSettings>> CreateAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var validated = SettingsValidators.ValidatePrinter(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var printer = await printers.InsertAsync(validated.Value, cancellationToken);
        LogSaved("added", printer);
        return printer;
    }

    /// <summary><c>PUT /:id</c>: id (400) → body (400) → unknown (404 <see cref="SettingsMessages.PrinterNotFound"/>) → save.</summary>
    public async Task<Result<PrinterSettings>> UpdateAsync(string id, JsonElement body, CancellationToken cancellationToken)
    {
        if (SettingsValidators.ValidatePrinterId(id) is { } idError)
        {
            return Error.Validation(idError);
        }

        var validated = SettingsValidators.ValidatePrinter(body);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var printer = await printers.UpdateAsync(id.ToLowerInvariant(), validated.Value, cancellationToken);
        if (printer is null)
        {
            return Error.NotFound(SettingsMessages.PrinterNotFound);
        }

        LogSaved("updated", printer);
        return printer;
    }

    /// <summary><c>DELETE /:id</c>: id (400) → unknown (404) → answers the deleted printer.</summary>
    public async Task<Result<PrinterSettings>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (SettingsValidators.ValidatePrinterId(id) is { } idError)
        {
            return Error.Validation(idError);
        }

        var printer = await printers.DeleteAsync(id.ToLowerInvariant(), cancellationToken);
        if (printer is null)
        {
            return Error.NotFound(SettingsMessages.PrinterNotFound);
        }

        LogSaved("deleted", printer);
        return printer;
    }

    private void LogSaved(string action, PrinterSettings printer) =>
        LogPrinter(printer.Id, action, printer.Ip, printer.Port, printer.TemplateName);

    [LoggerMessage(EventId = 50, Level = LogLevel.Information,
        Message = "Printer {PrinterId} {Action}: {PrinterIp}:{PrinterPort}, template {TemplateName}")]
    private partial void LogPrinter(string printerId, string action, string? printerIp, double? printerPort, string? templateName);
}
