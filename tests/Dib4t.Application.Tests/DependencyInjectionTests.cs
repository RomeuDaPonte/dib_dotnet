using Dib4t.Application.Account;
using Dib4t.Application.Budgets;
using Dib4t.Application.Database;
using Dib4t.Application.Entities;
using Dib4t.Application.Logs;
using Dib4t.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Dib4t.Application.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void Use_cases_resolve_and_the_operation_lock_is_one_per_process()
    {
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton(Substitute.For<IDatabaseBackupService>());
        services.AddSingleton(Substitute.For<IPreRestoreBackupStore>());
        services.AddSingleton(Substitute.For<IUserRepository>());
        services.AddSingleton(Substitute.For<IPasswordHasher>());
        services.AddSingleton(Substitute.For<ITokenService>());
        services.AddSingleton(Substitute.For<IPricesRepository>());
        services.AddSingleton(Substitute.For<IPaymentMethodRepository>());
        services.AddSingleton(Substitute.For<IPrinterSettingsRepository>());
        services.AddSingleton(Substitute.For<ILogQueryService>());
        services.AddSingleton(Substitute.For<IEntityRepository>());
        services.AddSingleton(Substitute.For<IBudgetRepository>());
        services.AddSingleton(Substitute.For<IBudgetTaskRepository>());
        services.AddSingleton(Substitute.For<IBudgetPdfGenerator>());
        services.AddSingleton(Substitute.For<Nas.INasFileShare>());
        services.AddSingleton(Substitute.For<Dib4t.Application.Jobs.IJobRepository>());
        services.AddSingleton(Substitute.For<Printing.ILabelPrinter>());
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseRestoreService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<AccountService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<UserAdministrationService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<PricesService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<PaymentMethodsService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<PrinterSettingsService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<DefaultDataSeeder>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<LogsService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<EntitiesService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<BudgetsService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<BudgetPdfService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<Dib4t.Application.Jobs.JobsService>().Should().NotBeNull();
        }

        provider.GetRequiredService<DatabaseOperationLock>().Should().BeSameAs(provider.GetRequiredService<DatabaseOperationLock>());
        // The restore outlives its request (PLAN.md §7.6): one service and one progress per process.
        provider.GetRequiredService<DatabaseRestoreService>().Should().BeSameAs(provider.GetRequiredService<DatabaseRestoreService>());
        provider.GetRequiredService<RestoreTracker>().Should().BeSameAs(provider.GetRequiredService<RestoreTracker>());
        // One numbering lock per process (PLAN.md §4.7).
        provider.GetRequiredService<BudgetNumbering>().Should().BeSameAs(provider.GetRequiredService<BudgetNumbering>());
        // One cache per process: the 30 s window is shared by every request (PLAN.md §4.6).
        provider.GetRequiredService<UserSessionCache>().Should().BeSameAs(provider.GetRequiredService<UserSessionCache>());
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
