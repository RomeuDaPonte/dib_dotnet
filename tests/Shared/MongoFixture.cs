using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Dib4t.Tests.Common;

/// <summary>
/// One <c>mongo:9.0.8</c> container (same image as production, PLAN.md §2.2) per test class; each test takes its
/// own database (<see cref="NewDatabaseName"/>) so tests never see each other's data.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    public const string Image = "mongo:9.0.8";

    private readonly MongoDbContainer _container = new MongoDbBuilder(Image).Build();
    private int _databases;

    public IMongoClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        Client = new MongoClient(ConnectionString("admin"));
    }

    /// <summary>Connection string as the app gets it in <c>Mongo:ConnectionString</c> (standalone: <c>retryWrites=false</c>).</summary>
    public string ConnectionString(string databaseName) => new MongoUrlBuilder(_container.GetConnectionString())
    {
        DatabaseName = databaseName,
        AuthenticationSource = "admin",
        RetryWrites = false,
    }.ToString();

    public string NewDatabaseName() => $"test{Interlocked.Increment(ref _databases):D3}";

    public IMongoDatabase NewDatabase() => Client.GetDatabase(NewDatabaseName());

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
