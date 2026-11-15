namespace Dib4t.Domain.Settings;

/// <summary>A label printer for one print template (legacy <c>models/printerSettings.js</c>).</summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="Ip">IPv4/IPv6 address as entered (Joi <c>ip()</c>, CIDR allowed).</param>
/// <param name="Port">TCP port (any number: the legacy validation has no range).</param>
/// <param name="TemplateName">One of <c>PrintTemplates</c>.</param>
/// <param name="Version">The stored <c>__v</c>.</param>
public sealed record PrinterSettings(string Id, string? Ip, double? Port, string? TemplateName, int Version);
