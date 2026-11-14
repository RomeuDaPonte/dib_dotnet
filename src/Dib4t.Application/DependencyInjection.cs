using Dib4t.Application.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Dib4t.Application;

/// <summary>Registers the use-case services (ports are registered by Infrastructure).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseOperationLock>();
        services.AddScoped<DatabaseRestoreService>();
        return services;
    }
}
