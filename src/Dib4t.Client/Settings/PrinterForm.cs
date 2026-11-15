using System.Text.RegularExpressions;
using Dib4t.Client.Forms;
using Dib4t.Contracts.Settings;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Settings;

/// <summary>
/// The printer form (legacy <c>settings/printers.jsx</c>, yup <c>printerSettingsSchema</c>): IP (IPv4 regex
/// "IP inválido", then required), Porta (<c>number().required()</c>), Template (<c>oneOf(producao, maquinacao)</c>).
/// The first message of a field is shown, as Formik does.
/// </summary>
public sealed partial class PrinterForm
{
    /// <summary>The template options: value and label.</summary>
    public static IReadOnlyList<(string Value, string Label)> Templates { get; } =
        [(PrintTemplates.Producao, "Produção"), (PrintTemplates.Maquinacao, "Maquinação")];

    /// <summary>The printer being edited; <c>null</c> for a new one.</summary>
    public string? Id { get; private init; }

    public string? Ip { get; set; }

    /// <summary>The port field's text (an <c>input type="number"</c>).</summary>
    public string? Port { get; set; }

    public string? TemplateName { get; set; }

    public static PrinterForm New() => new() { Ip = string.Empty, Port = string.Empty, TemplateName = string.Empty };

    public static PrinterForm Edit(PrinterSettingsResponse printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return new PrinterForm
        {
            Id = printer.Id,
            Ip = printer.Ip ?? string.Empty,
            Port = printer.Port is { } port ? JsNumber.ToString(port) : string.Empty,
            TemplateName = printer.TemplateName ?? string.Empty,
        };
    }

    public PrinterFormErrors Validate()
    {
        var ip = !Ipv4().IsMatch(Ip ?? string.Empty) ? "IP inválido" : null;
        var port = Port is null ? Yup.Required("Porta") : Yup.Number(Port) is null ? Yup.NumberTypeError("Porta", Port) : null;
        var template = string.IsNullOrEmpty(TemplateName) || !PrintTemplates.All.Contains(TemplateName, StringComparer.Ordinal)
            ? "Template must be one of the following values: producao, maquinacao"
            : null;
        return new PrinterFormErrors(ip, port, template);
    }

    /// <summary>Formik sends a number field as a number.</summary>
    public PrinterSettingsRequest ToRequest() => new(Ip ?? string.Empty, Yup.Number(Port) ?? 0, TemplateName ?? string.Empty);

    [GeneratedRegex(@"^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\z")]
    private static partial Regex Ipv4();
}

/// <summary>Per-field messages shown under the inputs.</summary>
public sealed record PrinterFormErrors(string? Ip, string? Port, string? TemplateName)
{
    public static PrinterFormErrors None { get; } = new(null, null, null);

    public bool Any => Ip is not null || Port is not null || TemplateName is not null;
}
