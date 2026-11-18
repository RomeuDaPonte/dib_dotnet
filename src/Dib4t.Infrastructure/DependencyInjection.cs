using Dib4t.Application.Account;
using Dib4t.Application.Budgets;
using Dib4t.Application.Database;
using Dib4t.Application.Entities;
using Dib4t.Application.Jobs;
using Dib4t.Application.Settings;
using Dib4t.Infrastructure.Auth;
using Dib4t.Infrastructure.Backup;
using Dib4t.Infrastructure.Logging;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Repositories;
using Dib4t.Infrastructure.Nas;
using Dib4t.Infrastructure.Pdf;
using Dib4t.Infrastructure.Printing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddScoped<IUserRepository, MongoUserRepository>();
        services.AddScoped<IPricesRepository, MongoPricesRepository>();
        services.AddScoped<IPaymentMethodRepository, MongoPaymentMethodRepository>();
        services.AddScoped<IPrinterSettingsRepository, MongoPrinterSettingsRepository>();
        services.AddScoped<IEntityRepository, MongoEntityRepository>();
        services.AddScoped<IBudgetRepository, MongoBudgetRepository>();
        services.AddScoped<IBudgetTaskRepository, MongoBudgetTaskRepository>();
        services.AddScoped<IJobRepository, MongoJobRepository>();
        services.AddSingleton<IBudgetPdfGenerator, PdfsharpBudgetPdfGenerator>();
        services.AddNas(configuration);
        services.AddLabelPrinter(configuration);
        services.AddAuthAdapters(configuration);
        return services;
    }

    /// <summary>
    /// The legacy startup seed (default prices, administrator; <see cref="StartupSeeding"/>). Separate from
    /// <see cref="AddInfrastructure"/> because it runs a use case: the app's composition root adds it with
    /// <c>AddApplication()</c>.
    /// </summary>
    public static IServiceCollection AddStartupSeeding(this IServiceCollection services)
    {
        services.AddHostedService<StartupSeeding>();
        return services;
    }

    private static void AddAuthAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Key.Length == 0 || options.KeyBytes().Length >= JwtOptions.MinimumKeyBytes,
                $"Jwt:Key must be at least {JwtOptions.MinimumKeyBytes} bytes (256 bits).")
            .Validate<IHostEnvironment>(
                (options, environment) => !environment.IsProduction()
                    || !options.Key.StartsWith(JwtOptions.DevelopmentKeyPrefix, StringComparison.Ordinal),
                "Jwt:Key is the development key; set the production key in Jwt__Key.")
            .ValidateOnStart();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
    }
}
