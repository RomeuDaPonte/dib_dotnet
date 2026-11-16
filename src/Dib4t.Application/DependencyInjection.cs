using Dib4t.Application.Account;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Application.Database;
using Dib4t.Application.Entities;
using Dib4t.Application.Logs;
using Dib4t.Application.Settings;
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
        services.AddScoped<UserAdministrationService>();
        services.AddScoped<PricesService>();
        services.AddScoped<PaymentMethodsService>();
        services.AddScoped<PrinterSettingsService>();
        services.AddScoped<EntitiesService>();
        services.AddOptions<AppOptions>();
        services.AddSingleton<BudgetNumbering>();
        services.AddScoped<BudgetsService>();
        services.AddScoped<DefaultDataSeeder>();
        services.AddSingleton<DatabaseOperationLock>();
        services.AddScoped<DatabaseRestoreService>();
        services.AddScoped<LogsService>();
        return services;
    }
}
