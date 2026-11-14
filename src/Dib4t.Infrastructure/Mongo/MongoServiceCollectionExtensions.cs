using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

internal static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// One <see cref="IMongoClient"/> per process, created on first use: the host starts without a reachable database
    /// (static files, health of the process). The connection string is validated at startup; <c>retryWrites</c> is
    /// always off (standalone server, PLAN.md §2.2). Every command is logged by <see cref="MongoCommandLogger"/>
    /// (§3.3); the server version is checked in the background (<see cref="MongoServerVersionCheck"/>). The class maps
    /// of the legacy model are registered here, before any document is read or written (<see cref="MongoMappings"/>).
    /// </summary>
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        MongoMappings.Register();

        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => IsValidConnectionString(options.ConnectionString), "Mongo:ConnectionString is not a valid MongoDB connection string.")
            .ValidateOnStart();

        services.AddSingleton<MongoCommandLogger>();
        services.AddSingleton<IMongoClient>(provider =>
        {
            var settings = MongoClientSettings.FromConnectionString(
                provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            settings.RetryWrites = false;
            settings.ClusterConfigurator = provider.GetRequiredService<MongoCommandLogger>().Subscribe;
            return new MongoClient(settings);
        });
        services.AddSingleton(provider =>
        {
            var url = MongoUrl.Create(provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            return provider.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? MongoOptions.DefaultDatabaseName);
        });
        services.AddHostedService<MongoServerVersionCheck>();
        return services;
    }

    private static bool IsValidConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return true; // [Required] reports it.
        }

        try
        {
            _ = MongoUrl.Create(connectionString);
            return true;
        }
        catch (MongoConfigurationException)
        {
            return false;
        }
    }
}
