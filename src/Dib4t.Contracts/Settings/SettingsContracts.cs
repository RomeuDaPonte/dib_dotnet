using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Settings;

/// <summary>
/// <c>GET</c> / <c>POST /api/prices</c> answer, legacy <c>_.pick</c> order: automation, consultancy, development,
/// machining, assembly, margin (a field the document lacks is left out; no document → <c>{}</c>).
/// </summary>
public sealed record PricesResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Automation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Consultancy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Development,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Machining,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Assembly,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Margin);

/// <summary>
/// <c>POST /api/prices</c> body, in the legacy service's order (<c>pricesService.js</c>). The values are the texts of
/// the dialog's fields, as the legacy Formik form sends them: the server converts them (Joi), so what is stored is
/// what the legacy app stores for the same input.
/// </summary>
public sealed record PricesRequest(string Automation, string Consultancy, string Development, string Machining, string Margin, string Assembly);

/// <summary>A payment method as the legacy API answers it: the stored document <c>{ _id, name, __v }</c>.</summary>
public sealed record PaymentMethodResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("__v")] int Version);

/// <summary><c>POST /api/entity/addPaymentMethod</c> body.</summary>
public sealed record PaymentMethodRequest(string Name);

/// <summary>A printer as the legacy API answers it: the stored document <c>{ _id, ip, port, templateName, __v }</c>.</summary>
public sealed record PrinterSettingsResponse(
    [property: JsonPropertyName("_id")] string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ip,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Port,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TemplateName,
    [property: JsonPropertyName("__v")] int Version);

/// <summary><c>POST /api/printer-settings</c> and <c>PUT /:id</c> body (legacy form order: ip, port, templateName).</summary>
public sealed record PrinterSettingsRequest(string Ip, double Port, string TemplateName);
