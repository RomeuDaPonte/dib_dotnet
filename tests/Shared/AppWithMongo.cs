using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Dib4t.Tests.Common;

/// <summary>
/// The new app (in-memory <see cref="WebApplicationFactory{TEntryPoint}"/>, environment <c>Development</c>) on its
/// own <c>mongo:9.0.8</c> database and its own temporary log store file, one per test class.
/// </summary>
public class AppWithMongo : IAsyncLifetime
{
    private readonly MongoFixture _mongo = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public IMongoDatabase Database { get; private set; } = null!;

    /// <summary>The app's log store (PLAN.md §3.3), deleted with the fixture.</summary>
    public string LogStorePath { get; } = Path.Combine(Path.GetTempPath(), $"dib4t-test-logs-{Guid.NewGuid():N}.db");

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        var name = _mongo.NewDatabaseName();
        Database = _mongo.Client.GetDatabase(name);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseEnvironment("Development")
                .UseSetting("Mongo:ConnectionString", _mongo.ConnectionString(name))
                .UseSetting("Logs:Path", LogStorePath);
            ConfigureWebHost(builder);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _mongo.DisposeAsync();
        foreach (var file in new[] { LogStorePath, LogStorePath + "-wal", LogStorePath + "-shm" })
        {
            File.Delete(file);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A client whose requests are signed as <paramref name="user"/> (saved to the database before each one).</summary>
    public HttpClient CreateClient(TestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Factory.CreateDefaultClient(new TestUserHandler(user, Database, user.TokenFrom(Factory.Services)));
    }

    /// <summary>Extra test configuration (e.g. test-only endpoints).</summary>
    protected virtual void ConfigureWebHost(IWebHostBuilder builder)
    {
    }
}
