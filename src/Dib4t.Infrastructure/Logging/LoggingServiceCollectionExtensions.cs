using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddOptions<LogsOptions>()
            .Bind(configuration.GetSection(LogsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<LogsOptions>>().Value;
            var path = Path.IsPathRooted(options.Path)
                ? options.Path
                : Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, options.Path);
            return new LogStoreSink(new SqliteLogStore(path), options, provider.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        return services;
    }
}
