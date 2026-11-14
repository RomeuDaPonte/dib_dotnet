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
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseRestoreService>().Should().NotBeNull();
        }

        provider.GetRequiredService<DatabaseOperationLock>().Should().BeSameAs(provider.GetRequiredService<DatabaseOperationLock>());
    }
}
