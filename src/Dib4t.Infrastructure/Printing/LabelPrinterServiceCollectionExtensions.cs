using Dib4t.Application.Printing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Printing;

/// <summary>
/// <see cref="ILabelPrinter"/>: TCP in Production with <c>LabelPrinter:Enabled</c> (default on there), otherwise the
/// adapter that only logs (PLAN.md §5, §4.7).
/// </summary>
public static partial class LabelPrinterServiceCollectionExtensions
{
    public static IServiceCollection AddLabelPrinter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<LabelPrinterOptions>()
            .Bind(configuration.GetSection(LabelPrinterOptions.SectionName))
            .Validate(options => options.ConnectTimeoutMilliseconds > 0 && options.SendTimeoutMilliseconds > 0,
                "LabelPrinter: the timeouts must be positive.")
            .ValidateOnStart();
        services.AddSingleton<ILabelPrinter>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<LabelPrinterOptions>>().Value;
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LabelPrinterServiceCollectionExtensions).FullName!);
            if (options.IsEnabledIn(environment))
            {
                LogTcp(logger);
                return ActivatorUtilities.CreateInstance<TcpLabelPrinter>(provider);
            }

            var reason = environment.IsProduction() ? "LabelPrinter:Enabled=false" : $"environment {environment.EnvironmentName}";
            LogOff(logger, reason);
            return ActivatorUtilities.CreateInstance<LoggingLabelPrinter>(provider, reason);
        });
        return services;
    }

    [LoggerMessage(EventId = 165, Level = LogLevel.Information, Message = "Label printing over TCP to the printers of the settings")]
    private static partial void LogTcp(ILogger logger);

    [LoggerMessage(EventId = 166, Level = LogLevel.Information, Message = "Label printing off ({Reason}): labels are only logged")]
    private static partial void LogOff(ILogger logger, string reason);
}
