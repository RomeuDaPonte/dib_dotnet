using Dib4t.Application.Account;
using Dib4t.Application.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dib4t.Application;

/// <summary>Registers the use-case services (ports are registered by Infrastructure).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<UserSessionOptions>();
        services.AddSingleton<UserSessionCache>();
        services.AddScoped<AccountService>();
        services.AddSingleton<DatabaseOperationLock>();
        services.AddScoped<DatabaseRestoreService>();
        return services;
    }
}
