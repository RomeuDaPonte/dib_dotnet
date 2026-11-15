using Dib4t.Application.Account;
using Dib4t.Application.Database;
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
        services.AddSingleton(Substitute.For<IUserRepository>());
        services.AddSingleton(Substitute.For<IPasswordHasher>());
        services.AddSingleton(Substitute.For<ITokenService>());
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseRestoreService>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<AccountService>().Should().NotBeNull();
        }

        provider.GetRequiredService<DatabaseOperationLock>().Should().BeSameAs(provider.GetRequiredService<DatabaseOperationLock>());
        // One cache per process: the 30 s window is shared by every request (PLAN.md §4.6).
        provider.GetRequiredService<UserSessionCache>().Should().BeSameAs(provider.GetRequiredService<UserSessionCache>());
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
