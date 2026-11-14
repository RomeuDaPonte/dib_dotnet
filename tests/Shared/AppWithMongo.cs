using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Dib4t.Tests.Common;

/// <summary>
/// The new app (in-memory <see cref="WebApplicationFactory{TEntryPoint}"/>, environment <c>Development</c>) on its
/// own <c>mongo:9.0.8</c> database, one per test class.
/// </summary>
public sealed class AppWithMongo : IAsyncLifetime
{
    private readonly MongoFixture _mongo = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public IMongoDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        var name = _mongo.NewDatabaseName();
        Database = _mongo.Client.GetDatabase(name);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("Mongo:ConnectionString", _mongo.ConnectionString(name)));
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _mongo.DisposeAsync();
    }
}
