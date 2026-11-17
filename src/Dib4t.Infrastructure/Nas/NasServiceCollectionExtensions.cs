using Dib4t.Application.Nas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// <see cref="INasFileShare"/>: SSH in Production with <c>Nas:Enabled</c> (default on there), otherwise the adapter
/// that only logs (PLAN.md §5, §4.7). With SSH on, host, user and password are checked at startup.
/// </summary>
public static partial class NasServiceCollectionExtensions
{
    public static IServiceCollection AddNas(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<NasOptions>()
            .Bind(configuration.GetSection(NasOptions.SectionName))
            .Validate(options => options.Port is > 0 and < 65536 && options.ConnectTimeoutSeconds > 0 && options.CommandTimeoutSeconds > 0,
                "Nas: Port must be 1–65535 and the timeouts positive.")
            .Validate<IHostEnvironment>(
                (options, environment) => !options.IsEnabledIn(environment)
                    || !(string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrEmpty(options.UserName) || string.IsNullOrEmpty(options.Password)),
                "Nas: Host, UserName and Password (Nas__Password) are required in Production; set Nas__Enabled=false to run without the NAS.")
            .ValidateOnStart();
        services.AddSingleton<INasFileShare>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<NasOptions>>().Value;
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NasServiceCollectionExtensions).FullName!);
            if (options.IsEnabledIn(environment))
            {
                LogSsh(logger, options.Host, options.Port, options.UserName);
                return ActivatorUtilities.CreateInstance<SshNasFileShare>(provider);
            }

            var reason = environment.IsProduction() ? "Nas:Enabled=false" : $"environment {environment.EnvironmentName}";
            LogOff(logger, reason);
            return ActivatorUtilities.CreateInstance<LoggingNasFileShare>(provider, reason);
        });
        return services;
    }

    [LoggerMessage(EventId = 142, Level = LogLevel.Information, Message = "NAS folder operations over SSH to {Host}:{Port} as {SshUser}")]
    private static partial void LogSsh(ILogger logger, string host, int port, string sshUser);

    [LoggerMessage(EventId = 143, Level = LogLevel.Information, Message = "NAS folder operations off ({Reason}): commands are only logged")]
    private static partial void LogOff(ILogger logger, string reason);
}
