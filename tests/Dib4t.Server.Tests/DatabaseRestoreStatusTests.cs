using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dib4t.Application.Database;
using Dib4t.Contracts.Database;
using Dib4t.Tests.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests;

/// <summary>
/// The restore hardened for legacy imports (PLAN.md §7.6, §10 Phase 5 step 4), over HTTP on a real mongo:9.0.8:
/// the automatic pre-restore backup in the server's folder, the built-in verification and the 9.0 adjustments in
/// <c>GET /api/database/restore/status</c>, and a restore that continues when the client goes away.
/// </summary>
public sealed class DatabaseRestoreStatusTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly Uri StatusUri = new("/api/database/restore/status", UriKind.Relative);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_is_204_until_a_restore_ran()
    {
        await using var host = app.Factory.WithWebHostBuilder(_ => { });
        using var client = Client(host);

        using var response = await client.GetAsync(StatusUri, Cancellation);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_restore_keeps_the_previous_database_in_the_backup_folder_and_verifies_itself()
    {
        await using var host = app.Factory.WithWebHostBuilder(_ => { });
        using var client = Client(host);
        await PostAsync(client, DibbakTestWriter.Valid(("before", [new BsonDocument { { "_id", 1 }, { "price", 10 } }])).ToFile());
        var previous = await SnapshotAsync();
        var file = await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation);

        using var response = await PostAsync(client, file, "restore-42");
        var status = await StatusAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        status.Should().BeEquivalentTo(new
        {
            Id = "restore-42",
            Stage = "completed",
            Running = false,
            Bytes = (long)file.Length,
            Collections = (int?)8,
            Documents = (long?)30_754,
            CollectionsDone = 8,
            DocumentsDone = 30_754L,
            CurrentCollection = (string?)null,
            Error = (string?)null,
            DatabaseChanged = false,
        });
        status.Adjustments.Should().BeEmpty("the production backup has only _id indexes and no options");
        status.Verification!.HasDifferences.Should().BeFalse();
        status.Verification.Collections.Select(c => (c.Name, c.Matches)).Should().HaveCount(8).And.OnlyContain(c => c.Matches);
        status.FinishedAt.Should().BeOnOrAfter(status.StartedAt);

        // The automatic backup holds exactly the database as it was before the restore.
        status.PreRestoreBackup.Should().MatchRegex(@"^dib4t-pre-restore-\d{4}-\d\d-\d\dT\d\d-\d\d-\d\d-\d{3}Z\.dibbak$");
        var saved = DibbakFile.Read(Path.Combine(app.BackupDirectory, status.PreRestoreBackup!));
        // (but for "users": the test client saves its signed-in user before each request)
        saved.ToSnapshot().DiffFrom(previous).Where(d => !d.StartsWith("users", StringComparison.Ordinal)).Should().BeEmpty();
        saved.Collections.Select(c => c.Name).Should().Contain("before");
        saved.Header.GetProperty("createdBy").GetString().Should().Be(TestUser.Admin.Email);
        (await SnapshotAsync()).DiffFrom(DibbakFile.Parse(file).ToSnapshot()).Where(d => !d.StartsWith("users", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_file_is_in_the_status_with_the_legacy_message_and_no_backup_is_made()
    {
        await using var host = app.Factory.WithWebHostBuilder(_ => { });
        using var client = Client(host);
        var before = Directory.Exists(app.BackupDirectory) ? Directory.GetFiles(app.BackupDirectory).Length : 0;

        using var response = await PostAsync(client, "this is not a backup"u8.ToArray());
        var status = await StatusAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (status.Stage, status.Error, status.PreRestoreBackup, status.DatabaseChanged).Should()
            .Be(("rejected", "O ficheiro não é um backup válido da Dib4t.", null, false));
        (Directory.Exists(app.BackupDirectory) ? Directory.GetFiles(app.BackupDirectory).Length : 0).Should().Be(before);
    }

    [Fact]
    public async Task Status_shows_the_adjustments_for_MongoDB_9_0()
    {
        await using var host = app.Factory.WithWebHostBuilder(_ => { });
        using var client = Client(host);
        var file = new DibbakTestWriter().Magic().HeaderFrame()
            .CollectionFrame("users", options: """{"flags":1}""",
                indexes: """[{"v":2,"key":{"_id":1},"name":"_id_","ns":"dib4t.users"},{"v":2,"unique":true,"key":{"email":1},"name":"email_1","ns":"dib4t.users","background":true}]""")
            .DocumentFrame(new BsonDocument { { "_id", TestUser.Admin.Id }, { "email", "x@dib4t.local" } })
            .EndFrame(1, 1).ToFile();

        using var response = await PostAsync(client, file);
        var status = await StatusAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        status.Adjustments.Select(a => (a.Collection, a.Index, string.Join(",", a.Removed), a.IndexRemoved)).Should().Equal(
            ("users", null, "flags", false), ("users", "email_1", "background", false));
        status.Verification!.HasDifferences.Should().BeFalse();
    }

    /// <summary>
    /// A real disconnect: the upload is complete, the restore holds at its pre-restore backup, the client gives up.
    /// The server keeps the lock and finishes the restore; its progress and result stay readable.
    /// </summary>
    [Fact]
    public async Task A_client_that_goes_away_does_not_stop_the_restore()
    {
        var gate = new GatedStore();
        await using var host = app.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // The app's own store, decorated.
            var real = services.Single(d => d.ServiceType == typeof(IPreRestoreBackupStore));
            services.Remove(real);
            services.AddSingleton<IPreRestoreBackupStore>(provider =>
                gate.Wrapping((IPreRestoreBackupStore)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
        }));
        using var client = Client(host);
        var document = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "total", 10.5 } };
        var file = DibbakTestWriter.Valid(("budgets", [document])).ToFile();
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var waiting = PostAsync(client, file, "gone", leaving.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        (await StatusAsync(client)).Stage.Should().Be("preRestoreBackup");
        await leaving.CancelAsync();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        host.Services.GetRequiredService<DatabaseOperationLock>().BusyWith.Should().Be("restauro", "the restore goes on");
        gate.Release.SetResult();
        var status = await WaitForEndAsync(client);

        (status.Id, status.Stage, status.Verification!.HasDifferences).Should().Be(("gone", "completed", false));
        host.Services.GetRequiredService<DatabaseOperationLock>().BusyWith.Should().BeNull();
        (await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .Should().Equal(document);
        File.Exists(Path.Combine(app.BackupDirectory, status.PreRestoreBackup!)).Should().BeTrue();
    }

    [Fact]
    public async Task Status_needs_the_settings_role()
    {
        using var limited = app.CreateClient(TestUser.Limited);

        using var response = await limited.GetAsync(StatusUri, Cancellation);

        ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation)).Should().Be((401, "Não tem permissão para concluir esta acção."));
    }

    private HttpClient Client(WebApplicationFactory<Program> host) =>
        host.CreateDefaultClient(new TestUserHandler(TestUser.Admin, app.Database, TestUser.Admin.TokenFrom(host.Services)));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] body, string? id = null, CancellationToken? cancellationToken = null)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/database/restore") { Content = content };
        if (id is not null)
        {
            request.Headers.Add("x-restore-id", id);
        }

        return await client.SendAsync(request, cancellationToken ?? Cancellation);
    }

    private static async Task<RestoreStatusResponse> StatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<RestoreStatusResponse>(StatusUri, JsonSerializerOptions.Web, Cancellation))!;

    private static async Task<RestoreStatusResponse> WaitForEndAsync(HttpClient client)
    {
        for (var i = 0; i < 300; i++)
        {
            var status = await StatusAsync(client);
            if (!status.Running)
            {
                return status;
            }

            await Task.Delay(100, Cancellation);
        }

        throw new TimeoutException("the restore did not end");
    }

    private async Task<DbSnapshot> SnapshotAsync()
    {
        var collections = new Dictionary<string, IEnumerable<BsonDocument>>();
        foreach (var name in (await (await app.Database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation))
                 .Where(n => !n.StartsWith("system.", StringComparison.Ordinal)))
        {
            collections[name] = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        }

        return DbSnapshot.From(collections);
    }

    /// <summary>Holds the restore at its pre-restore backup until the test releases it.</summary>
    private sealed class GatedStore
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPreRestoreBackupStore Wrapping(IPreRestoreBackupStore inner) => new Gated(this, inner);

        private sealed class Gated(GatedStore gate, IPreRestoreBackupStore inner) : IPreRestoreBackupStore
        {
            public async Task<string> SaveAsync(Func<Stream, Task> write, DateTime createdAt)
            {
                gate.Reached.TrySetResult();
                await gate.Release.Task;
                return await inner.SaveAsync(write, createdAt);
            }
        }
    }
}
