using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

internal static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// One <see cref="IMongoClient"/> per process, created on first use: the host starts without a reachable database
    /// (static files, health of the process). Every command is logged by <see cref="MongoCommandLogger"/>
    /// (PLAN.md §3.3). The startup version check comes with the Mongo infrastructure step (§10 Phase 1).
    /// </summary>
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<MongoCommandLogger>();
        services.AddSingleton<IMongoClient>(provider =>
        {
            var settings = MongoClientSettings.FromConnectionString(
                provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            settings.ClusterConfigurator = provider.GetRequiredService<MongoCommandLogger>().Subscribe;
            return new MongoClient(settings);
        });
        services.AddSingleton(provider =>
        {
            var url = MongoUrl.Create(provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            return provider.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? MongoOptions.DefaultDatabaseName);
        });
        return services;
    }
}
