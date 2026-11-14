using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Tests.Logging;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>
/// Every MongoDB command the app sends is one <c>Database</c> entry with collection and duration, and no command or
/// reply content (PLAN.md §3.3, §11.9), against a real <c>mongo:9.0.8</c>.
/// </summary>
public sealed class MongoCommandLoggerTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private const string Marker = "marker-4b1d-no-content-in-logs";
    private static readonly string LoggerName = typeof(MongoCommandLogger).FullName!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_command_is_one_entry_with_database_collection_duration_and_result_size()
    {
        await using var app = Start();
        var budgets = app.Database.GetCollection<BsonDocument>("budgets");

        await budgets.InsertOneAsync(new BsonDocument { { "_id", 1 }, { "client", Marker } }, cancellationToken: Cancellation);
        await budgets.Find(new BsonDocument("client", Marker)).ToListAsync(Cancellation);
        await budgets.UpdateOneAsync(new BsonDocument("_id", 1), new BsonDocument("$set", new BsonDocument("note", Marker)), cancellationToken: Cancellation);
        await budgets.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Cancellation);
        await budgets.FindOneAndUpdateAsync(new BsonDocument("_id", 1), new BsonDocument("$inc", new BsonDocument("v", 1)), cancellationToken: Cancellation);
        await budgets.DeleteOneAsync(new BsonDocument("_id", 1), Cancellation);

        var entries = app.CommandEntries();
        entries.Select(entry => ((string)entry["CommandName"]!, entry["Result"])).Should().Equal(
            ("insert", "n=1"),
            ("find", "returned=1"),
            ("update", "n=1 nModified=1"),
            ("aggregate", "returned=1"),
            ("findAndModify", "n=1"),
            ("delete", "n=1"));
        entries.Should().AllSatisfy(entry =>
        {
            entry.Level.Should().Be(LogLevel.Information);
            entry["Database"].Should().Be(app.Database.DatabaseNamespace.DatabaseName);
            entry["Collection"].Should().Be("budgets");
            ((double)entry["DurationMs"]!).Should().BeGreaterThanOrEqualTo(0);
        });
        app.Logs.Entries.Should().AllSatisfy(entry =>
            (entry.Message + string.Join(' ', entry.Properties.Values)).Should().NotContain(Marker));
    }

    [Fact]
    public async Task getMore_is_logged_with_its_collection_and_batch_size()
    {
        await using var app = Start();
        var tasks = app.Database.GetCollection<BsonDocument>("tasks");
        await tasks.InsertManyAsync(Enumerable.Range(0, 5).Select(i => new BsonDocument("_id", i)), cancellationToken: Cancellation);

        await tasks.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { BatchSize = 2 }).ToListAsync(Cancellation);

        app.CommandEntries().Select(entry => ((string)entry["CommandName"]!, entry["Collection"], entry["Result"])).Should().Equal(
            ("insert", "tasks", "n=5"),
            ("find", "tasks", "returned=2"),
            ("getMore", "tasks", "returned=2"),
            ("getMore", "tasks", "returned=1"));
    }

    [Fact]
    public async Task A_failed_command_is_an_Error_with_the_server_error_code()
    {
        await using var app = Start();
        var budgets = app.Database.GetCollection<BsonDocument>("budgets");

        var act = () => budgets.Aggregate<BsonDocument>(
            new BsonDocument[] { new("$noSuchStage", Marker) }, cancellationToken: Cancellation).ToListAsync(Cancellation);

        await act.Should().ThrowAsync<MongoCommandException>();
        var entry = app.CommandEntries().Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.EventId.Should().Be(102);
        entry["CommandName"].Should().Be("aggregate");
        entry["ErrorCode"].Should().Be(40324);
        entry["ErrorType"].Should().Be(nameof(MongoCommandException));
    }

    [Fact]
    public async Task Write_errors_are_an_Error_with_the_first_code_but_without_the_document()
    {
        await using var app = Start();
        var users = app.Database.GetCollection<BsonDocument>("users");
        await users.InsertOneAsync(new BsonDocument("_id", Marker), cancellationToken: Cancellation);

        var act = () => users.InsertOneAsync(new BsonDocument("_id", Marker), cancellationToken: Cancellation);

        await act.Should().ThrowAsync<MongoWriteException>();
        var entry = app.CommandEntries()[^1];
        entry.Level.Should().Be(LogLevel.Error);
        entry.EventId.Should().Be(103);
        entry["WriteErrors"].Should().Be(1);
        entry["ErrorCode"].Should().Be(11000L);
        entry.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Commands_slower_than_the_threshold_are_Warnings()
    {
        await using var app = Start(slowCommandMs: 1);
        var tasks = app.Database.GetCollection<BsonDocument>("tasks");

        await tasks.InsertManyAsync(Enumerable.Range(0, 20_000).Select(i => new BsonDocument { { "_id", i }, { "text", new string('x', 100) } }),
            cancellationToken: Cancellation);

        app.CommandEntries().Should().NotBeEmpty().And.AllSatisfy(entry =>
        {
            entry.Level.Should().Be(LogLevel.Warning);
            entry.Message.Should().StartWith("Slow Mongo insert");
        });
    }

    [Theory]
    [InlineData("find", """{ "find": "budgets", "filter": {} }""", "budgets")]
    [InlineData("getMore", """{ "getMore": { "$numberLong": "5" }, "collection": "tasks" }""", "tasks")]
    [InlineData("aggregate", """{ "aggregate": 1, "pipeline": [] }""", null)]
    [InlineData("listCollections", """{ "listCollections": 1 }""", null)]
    [InlineData("saslStart", """{ }""", null)]
    public void Collection_is_read_from_the_command_name_only(string commandName, string command, string? collection) =>
        MongoCommandLogger.CollectionOf(commandName, BsonDocument.Parse(command)).Should().Be(collection);

    private TestApp Start(int slowCommandMs = 500)
    {
        var name = mongo.NewDatabaseName();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = mongo.ConnectionString(name),
            ["Logs:SlowCommandMs"] = slowCommandMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
        return new TestApp(services, logs, services.GetRequiredService<IMongoDatabase>());
    }

    private sealed record TestApp(ServiceProvider Services, RecordingLoggerProvider Logs, IMongoDatabase Database) : IAsyncDisposable
    {
        public IReadOnlyList<RecordedLog> CommandEntries() => [.. Logs.Entries.Where(entry => entry.Category == LoggerName)];

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
