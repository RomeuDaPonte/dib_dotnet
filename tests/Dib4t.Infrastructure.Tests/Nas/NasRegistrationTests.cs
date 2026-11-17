using Dib4t.Application.Nas;
using Dib4t.Infrastructure.Nas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Tests.Nas;

/// <summary>
/// Which NAS adapter runs where (PLAN.md §5, §4.7, D5): SSH only in Production with <c>Nas:Enabled</c> (default on
/// there), the logging no-op everywhere else, whatever the configuration says; with SSH on, the credentials are
/// required at startup.
/// </summary>
public sealed class NasRegistrationTests
{
    private static readonly Dictionary<string, string?> Credentials = new(StringComparer.Ordinal)
    {
        ["Nas:Host"] = "192.0.2.10",
        ["Nas:UserName"] = "webapp",
        ["Nas:Password"] = "secret",
    };

    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Development", "true", false)]
    [InlineData("Staging", "true", false)]
    [InlineData("Production", null, true)]
    [InlineData("Production", "true", true)]
    [InlineData("Production", "false", false)]
    public void Ssh_runs_only_in_production_unless_it_is_switched_off(string environment, string? enabled, bool ssh)
    {
        var settings = new Dictionary<string, string?>(Credentials, StringComparer.Ordinal) { ["Nas:Enabled"] = enabled };

        using var provider = Build(environment, settings);
        var nas = provider.GetRequiredService<INasFileShare>();

        nas.IsEnabled.Should().Be(ssh);
        nas.Should().BeOfType(ssh ? typeof(SshNasFileShare) : typeof(LoggingNasFileShare));
        provider.GetRequiredService<INasFileShare>().Should().BeSameAs(nas);
    }

    [Fact]
    public void Production_with_ssh_needs_host_user_and_password_and_without_it_needs_nothing()
    {
        using var missing = Build("Production", new Dictionary<string, string?>(Credentials, StringComparer.Ordinal) { ["Nas:Password"] = null });
        using var off = Build("Production", new Dictionary<string, string?>(StringComparer.Ordinal) { ["Nas:Enabled"] = "false" });
        using var development = Build("Development", []);
        using var badPort = Build("Development", new Dictionary<string, string?>(StringComparer.Ordinal) { ["Nas:Port"] = "0" });

        var refused = () => missing.GetRequiredService<IOptions<NasOptions>>().Value;
        refused.Should().Throw<OptionsValidationException>().WithMessage("*Nas__Password*");
        off.GetRequiredService<IOptions<NasOptions>>().Value.Enabled.Should().BeFalse();
        development.GetRequiredService<IOptions<NasOptions>>().Value.Port.Should().Be(22);
        var port = () => badPort.GetRequiredService<IOptions<NasOptions>>().Value;
        port.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData("SHA256:abc+/def=", "abc+/def")]
    [InlineData(" sha256:abc ", "abc")]
    [InlineData("abc==", "abc")]
    [InlineData("", null)]
    public void The_pinned_fingerprint_is_read_as_ssh_keygen_prints_it(string configured, string? expected) =>
        new NasOptions { HostFingerprint = configured }.PinnedFingerprint().Should().Be(expected);

    private static ServiceProvider Build(string environment, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment });
        services.AddNas(configuration);
        return services.BuildServiceProvider();
    }
}
