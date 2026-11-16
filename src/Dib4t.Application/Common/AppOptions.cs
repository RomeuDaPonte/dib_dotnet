namespace Dib4t.Application.Common;

/// <summary>Configuration section <c>App</c>.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// The company's time zone (IANA id): the year of new budget numbers and, from the PDF step on, the document date
    /// (D23). Legacy uses the server's clock in UTC; 1 January starts at the same instant in Europe/Lisbon (winter
    /// time), so numbers are the same.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Lisbon";

    /// <summary>The zone, or <c>null</c> when this system does not know <see cref="TimeZone"/> (checked at startup).</summary>
    public TimeZoneInfo? FindTimeZone() => TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : null;
}
