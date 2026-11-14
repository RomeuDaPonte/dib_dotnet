using Dib4t.Application.Database;
using Dib4t.Infrastructure.Backup;
using Dib4t.Infrastructure.Logging;
using Dib4t.Infrastructure.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dib4t.Infrastructure;

/// <summary>Registers the adapters behind the Application ports.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddLogStore(configuration);
        services.AddMongo(configuration);
        services.AddScoped<IDatabaseBackupService, MongoDatabaseBackupService>();
        return services;
    }
}
