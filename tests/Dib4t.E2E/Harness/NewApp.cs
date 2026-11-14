using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Dib4t.E2E.Harness;

/// <summary>
/// The new app (Dib4t.Server, in-process on a real Kestrel port so a browser can reach it) on its own
/// <c>mongo:9.0.8</c> (Testcontainers, same image as production, PLAN.md §2.2). Environment <c>Development</c>:
/// no NAS, no printers (§5.1).
/// </summary>
public sealed class NewApp : IAppUnderTest, IAsyncDisposable
{
    public const string DatabaseName = "dib4t";

    private readonly MongoDbContainer _mongo = new MongoDbBuilder(HarnessOptions.NewMongoImage).Build();
    private WebApplicationFactory<Program>? _factory;
    private IMongoDatabase? _database;

    public string Name => "new";

    public Uri UiBaseUrl { get; private set; } = null!;

    public Uri ApiBaseUrl => new(UiBaseUrl, "/api/");

    /// <summary>Connection string of the new app's database (<c>Mongo:ConnectionString</c>, §12).</summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mongo.StartAsync(cancellationToken);
        ConnectionString = new MongoUrlBuilder(_mongo.GetConnectionString())
        {
            DatabaseName = DatabaseName,
            AuthenticationSource = "admin",
            RetryWrites = false, // §2.2: standalone server, as in production
        }.ToString();
        _database = new MongoClient(ConnectionString).GetDatabase(DatabaseName);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("Mongo:ConnectionString", ConnectionString));
        // A free port on the loopback interface (UseKestrel(0) falls back to the fixed default port 5000).
        _factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _factory.StartServer();

        var address = _factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        UiBaseUrl = new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
    }

    /// <summary>
    /// TEMPORARY (until the backup restore slice, PLAN.md §10 Phase 1): loads the backup's raw BSON documents straight
    /// into the new database. Replace with <c>POST /api/database/restore</c> once the endpoint exists, so the E2E
    /// scenarios exercise the app's own import path.
    /// </summary>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken)
    {
        var backup = DibbakFile.Read(backupPath);
        await _database!.Client.DropDatabaseAsync(DatabaseName, cancellationToken);
        foreach (var collection in backup.Collections)
        {
            await _database.CreateCollectionAsync(collection.Name, cancellationToken: cancellationToken);
            if (collection.Documents.Count == 0)
            {
                continue;
            }

            var target = _database.GetCollection<RawBsonDocument>(collection.Name);
            foreach (var batch in collection.Documents.Chunk(1000))
            {
                var documents = batch.Select(bytes => new RawBsonDocument(bytes)).ToList();
                try
                {
                    await target.InsertManyAsync(
                        documents,
                        new InsertManyOptions { IsOrdered = true, BypassDocumentValidation = true },
                        cancellationToken);
                }
                finally
                {
                    documents.ForEach(d => d.Dispose());
                }
            }
        }
    }

    public async Task<DbSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var collections = new Dictionary<string, IEnumerable<BsonDocument>>();
        var names = await (await _database!.ListCollectionNamesAsync(cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
        foreach (var name in names.Where(n => !n.StartsWith("system.", StringComparison.Ordinal)))
        {
            collections[name] = await _database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
        }

        return DbSnapshot.From(collections);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _mongo.DisposeAsync();
    }
}
