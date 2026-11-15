using Dib4t.Infrastructure.Tests.Logging;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>
/// Startup (PLAN.md §2.2, §3.2): the connection string is validated, <c>retryWrites</c> is off, and the background
/// version check logs <c>buildInfo.version</c>, stops the app on a server the driver does not support, and keeps
/// trying while the server is unreachable.
/// </summary>
public sealed class MongoStartupTests(MongoFixture mongo) : IClassFixture<MongoFixture>, IDisposable
{
    private const string VersionCheck = "Dib4t.Infrastructure.Mongo.MongoServerVersionCheck";
    private readonly List<string> _logStores = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logs_the_server_version_and_keeps_running()
    {
        using var host = Build(mongo.ConnectionString(mongo.NewDatabaseName()), out var logs);

        await host.StartAsync(Cancellation);
        var entry = await WaitForAsync(logs, 110);

        entry.Level.Should().Be(LogLevel.Information);
        entry["ServerVersion"].Should().Be("9.0.8");
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeFalse();
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Stops_the_app_on_a_server_the_driver_does_not_support()
    {
        // The legacy server version: MongoDB.Driver 3.x refuses it (wire version too old).
        await using var legacy = new ContainerBuilder(new DockerImage("mongo:4.1.4", new Platform("linux/amd64")))
            .WithPortBinding(27017, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("waiting for connections"))
            .Build();
        await legacy.StartAsync(Cancellation);
        using var host = Build(
            $"mongodb://{legacy.Hostname}:{legacy.GetMappedPublicPort(27017)}/dib4t?serverSelectionTimeoutMS=20000", out var logs);

        await host.StartAsync(Cancellation);
        var entry = await WaitForAsync(logs, 112);

        entry.Level.Should().Be(LogLevel.Critical);
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Keeps_trying_while_the_server_is_unreachable()
    {
        using var host = Build("mongodb://127.0.0.1:1/dib4t?serverSelectionTimeoutMS=200&connectTimeoutMS=200", out var logs,
            retryInterval: "00:00:00.050");

        await host.StartAsync(Cancellation);
        await WaitForAsync(logs, 113, count: 2);

        logs.Entries.Where(e => e.Category == VersionCheck && e.EventId == 113).Should().AllSatisfy(e => e.Level.Should().Be(LogLevel.Error));
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeFalse();
        await host.StopAsync(Cancellation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-connection-string")]
    public async Task An_invalid_connection_string_stops_the_start(string connectionString)
    {
        using var host = Build(connectionString, out _);

        var start = () => host.StartAsync(Cancellation);

        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact]
    public void Retryable_writes_are_always_off()
    {
        using var host = Build(mongo.ConnectionString("dib4t").Replace("retryWrites=false", "retryWrites=true", StringComparison.Ordinal), out _);

        host.Services.GetRequiredService<IMongoClient>().Settings.RetryWrites.Should().BeFalse();
    }

    public void Dispose()
    {
        _logStores.ForEach(LogStoreFile.Delete);
    }

    private IHost Build(string connectionString, out RecordingLoggerProvider logs, string retryInterval = "00:00:30")
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = connectionString,
            ["Mongo:StartupCheckRetryInterval"] = retryInterval,
            ["Logs:Path"] = NewLogStore(),
            ["Jwt:Key"] = new string('k', 32), // validated at startup with the other options
        });
        var recording = new RecordingLoggerProvider();
        builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recording);
        builder.Services.AddInfrastructure(builder.Configuration);
        logs = recording;
        return builder.Build();
    }

    private string NewLogStore()
    {
        var path = LogStoreFile.NewTempPath();
        _logStores.Add(path);
        return path;
    }

    private static async Task<RecordedLog> WaitForAsync(RecordingLoggerProvider logs, int eventId, int count = 1)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var entries = logs.Entries.Where(e => e.Category == VersionCheck && e.EventId == eventId).ToList();
            if (entries.Count >= count)
            {
                return entries[0];
            }

            await Task.Delay(50, timeout.Token);
        }
    }
}
