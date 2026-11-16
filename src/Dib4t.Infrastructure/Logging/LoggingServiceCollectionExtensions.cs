using Dib4t.Application.Logs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dib4t.Infrastructure.Logging;

internal static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="LogsOptions"/> (validated at startup) and the <see cref="LogStoreSink"/> singleton that owns the
    /// SQLite file. The host adds the sink to its Serilog pipeline (Dib4t.Server).
    /// </summary>
    public static IServiceCollection AddLogStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System); // the cleanup schedule; AddApplication() registers the same
        services.AddOptions<LogsOptions>()
            .Bind(configuration.GetSection(LogsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Cleanup.FindTimeZone() is not null,
                "Logs:Cleanup:TimeZone is not a time zone this system knows (IANA id, e.g. Europe/Lisbon).")
            .Validate(options => options.Cleanup.ChunkSize > 0, "Logs:Cleanup:ChunkSize must be at least 1.")
            .ValidateOnStart();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<LogsOptions>>().Value;
            return new LogStoreLocation(Path.IsPathRooted(options.Path)
                ? options.Path
                : Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, options.Path));
        });
        services.AddSingleton(provider => new LogStoreSink(
            new SqliteLogStore(provider.GetRequiredService<LogStoreLocation>().Path),
            provider.GetRequiredService<IOptions<LogsOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ILogQueryService, SqliteLogQueryService>();
        services.AddSingleton<LogCleanupService>();
        services.AddHostedService(provider => provider.GetRequiredService<LogCleanupService>());
        return services;
    }
}
