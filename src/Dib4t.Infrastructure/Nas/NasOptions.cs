using Microsoft.Extensions.Hosting;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// Configuration section <c>Nas</c> (PLAN.md §5, §4.7). The SSH credentials come from the environment
/// (<c>Nas__Password</c>), never from a file in the repository; they are never logged.
/// </summary>
public sealed class NasOptions
{
    public const string SectionName = "Nas";

    /// <summary>
    /// Run the folder operations over SSH. Default: on in Production. Outside Production they are <b>always</b> only
    /// logged, whatever this says. Set <c>false</c> to run Production next to the old app without touching the NAS.
    /// </summary>
    public bool? Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The NAS's SSH host key as <c>ssh-keygen -lf</c> prints it (<c>SHA256:…</c>, the prefix is optional). When set,
    /// any other key is refused; when empty, any key is accepted (as legacy) and a warning names the key received.
    /// </summary>
    public string HostFingerprint { get; set; } = string.Empty;

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Whether the operations run over SSH in <paramref name="environment"/>.</summary>
    public bool IsEnabledIn(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsProduction() && Enabled != false;
    }

    /// <summary>The fingerprint without the <c>SHA256:</c> prefix and base64 padding (SSH.NET's format).</summary>
    internal string? PinnedFingerprint()
    {
        var value = (HostFingerprint ?? string.Empty).Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        value = value.TrimEnd('=');
        return value.Length == 0 ? null : value;
    }
}
