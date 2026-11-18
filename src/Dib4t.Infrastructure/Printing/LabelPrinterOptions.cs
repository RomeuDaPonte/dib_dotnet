using Microsoft.Extensions.Hosting;

namespace Dib4t.Infrastructure.Printing;

/// <summary>Configuration section <c>LabelPrinter</c> (PLAN.md §5, §4.7). The printers themselves are in <c>printersettings</c>.</summary>
public sealed class LabelPrinterOptions
{
    public const string SectionName = "LabelPrinter";

    /// <summary>
    /// Send labels to the printers. Default: on in Production. Outside Production they are <b>always</b> only logged,
    /// whatever this says. Set <c>false</c> to run Production next to the old app without printing.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Legacy <c>telnet-client</c> <c>timeout: 1500</c>.</summary>
    public int ConnectTimeoutMilliseconds { get; set; } = 1500;

    /// <summary>The longest the payload may take to be written and the connection closed.</summary>
    public int SendTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>Whether labels go to the printers in <paramref name="environment"/>.</summary>
    public bool IsEnabledIn(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsProduction() && Enabled != false;
    }
}
