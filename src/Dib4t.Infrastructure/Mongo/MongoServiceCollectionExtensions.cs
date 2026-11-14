using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo;

internal static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// One <see cref="IMongoClient"/> per process, created on first use: the host starts without a reachable database
    /// (static files, health of the process). The startup version check and the command logging come with the Mongo
    /// infrastructure step (PLAN.md §10 Phase 1).
    /// </summary>
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IMongoClient>(provider =>
            new MongoClient(provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(provider =>
        {
            var url = MongoUrl.Create(provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            return provider.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? MongoOptions.DefaultDatabaseName);
        });
        return services;
    }
}
