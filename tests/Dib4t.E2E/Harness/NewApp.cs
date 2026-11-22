using System.Net;
using System.Net.Http.Headers;
using Dib4t.SharedKernel.Auth;
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

    /// <summary>Short, so "a removed role takes effect without a new login" (PLAN.md §4.6) is quick to test.</summary>
    public static readonly TimeSpan UserCacheDuration = TimeSpan.FromSeconds(1);

    private readonly MongoDbContainer _mongo = new MongoDbBuilder(HarnessOptions.NewMongoImage).Build();
    private WebApplicationFactory<Program>? _factory;
    private IMongoDatabase? _database;
    private int _port;

    public string Name => "new";

    public Uri UiBaseUrl { get; private set; } = null!;

    public Uri ApiBaseUrl => new(UiBaseUrl, "/api/");

    /// <summary>Connection string of the new app's database (<c>Mongo:ConnectionString</c>, §12).</summary>
    public string ConnectionString { get; private set; } = null!;

    /// <summary>The new app's own database (test oracle and direct edits, e.g. removing a role).</summary>
    public IMongoDatabase Database => _database ?? throw new InvalidOperationException("not started");

    /// <summary>The new app's log store (PLAN.md §3.3): a temp file per run, deleted on dispose.</summary>
    public string LogStorePath { get; } = Path.Combine(Path.GetTempPath(), $"dib4t-e2e-logs-{Guid.NewGuid():N}.db");

    /// <summary>The app's backup folder (<c>Backup:Directory</c>: the automatic pre-restore backups), deleted with the harness.</summary>
    public string BackupDirectory { get; } = Path.Combine(Path.GetTempPath(), $"dib4t-e2e-backups-{Guid.NewGuid():N}");

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
        StartServer(port: 0);
    }

    /// <summary>
    /// Stops the server and starts a new one on the same port and database (same origin, so the browser keeps its
    /// localStorage): "still logged in after a server restart" (PLAN.md §4.6).
    /// </summary>
    public async Task RestartAsync()
    {
        await _factory!.DisposeAsync();
        StartServer(_port);
    }

    private void StartServer(int port)
    {
        // The key is the committed development key (appsettings.Development.json), the same across restarts.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("Mongo:ConnectionString", ConnectionString)
            .UseSetting("Logs:Path", LogStorePath)
            .UseSetting("Backup:Directory", BackupDirectory)
            .UseSetting("Auth:UserCacheDuration", UserCacheDuration.ToString()));
        // A free port on the loopback interface the first time (UseKestrel(0) falls back to the fixed port 5000).
        _factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        _factory.StartServer();

        var address = new Uri(_factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First());
        _port = address.Port;
        UiBaseUrl = new Uri($"http://localhost:{_port}/");
    }

    /// <summary>
    /// Restores through the app's own import path, <c>POST /api/database/restore</c> (PLAN.md §7.6), exactly as a user
    /// uploads a backup downloaded from the legacy app.
    /// </summary>
    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken) =>
        await RestoreAsync(await File.ReadAllBytesAsync(backupPath, cancellationToken), cancellationToken);

    /// <inheritdoc cref="RestoreAsync(string, CancellationToken)"/>
    /// <remarks>
    /// The endpoint needs the settings role: a harness user is saved first and signs the request; the restore then
    /// replaces it with the backup's users, so the database holds exactly the backup.
    /// </remarks>
    public async Task RestoreAsync(byte[] backup, CancellationToken cancellationToken)
    {
        await HarnessUser.SaveAsync(Database, cancellationToken);
        using var api = new HttpClient { BaseAddress = ApiBaseUrl, Timeout = TimeSpan.FromMinutes(5) };
        api.DefaultRequestHeaders.Add(AuthTexts.TokenHeader, HarnessUser.TokenFrom(_factory!.Services));
        using var content = new ByteArrayContent(backup);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await api.PostAsync(new Uri("database/restore", UriKind.Relative), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"new-app restore failed ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary><c>GET /api/database/restore/status</c> as the harness user (new app only, PLAN.md §7.6), or <see langword="null"/> (204).</summary>
    public async Task<Dib4t.Contracts.Database.RestoreStatusResponse?> RestoreStatusAsync(CancellationToken cancellationToken)
    {
        await HarnessUser.SaveAsync(Database, cancellationToken);
        using var api = new HttpClient { BaseAddress = ApiBaseUrl };
        api.DefaultRequestHeaders.Add(AuthTexts.TokenHeader, HarnessUser.TokenFrom(_factory!.Services));
        using var response = await api.GetAsync(new Uri("database/restore/status", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? null
            : await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Dib4t.Contracts.Database.RestoreStatusResponse>(
                response.Content, System.Text.Json.JsonSerializerOptions.Web, cancellationToken);
    }

    private static TestUser HarnessUser { get; } = TestUser.Admin with { Name = "E2E harness", Email = "e2e.harness@dib4t.local" };

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
        foreach (var file in new[] { LogStorePath, LogStorePath + "-wal", LogStorePath + "-shm" })
        {
            File.Delete(file);
        }

        if (Directory.Exists(BackupDirectory))
        {
            Directory.Delete(BackupDirectory, recursive: true);
        }
    }
}
