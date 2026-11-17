using System.Text.Json;
using Dib4t.Application.Budgets;
using Dib4t.Application.Common;
using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Budgets;

/// <summary>
/// The NAS folder steps of the budget routes, end to end (PLAN.md §5, §5.1): the app in <b>Production</b> with SSH
/// on, pointed at the fake NAS (never the real one), replays the budget scenarios of
/// <c>tests/fixtures/legacy/ssh/ssh-commands.json</c> (<c>tools/legacy/ssh-golden.js</c>, same requests, same clock)
/// and must give the legacy statuses, the same <c>ops</c> in the same order and the same folder tree, except for the
/// decided fixes D6, D20 and D21 (each stated below). The job scenarios are Phase 4.
/// </summary>
public sealed class BudgetNasGoldenTests(AppWithNas app) : IClassFixture<AppWithNas>
{
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Path.Combine(Fixtures.Root, "legacy", "ssh", "ssh-commands.json")));

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);

    public static TheoryData<string> Scenarios => [.. GoldenScenarios.All.Keys];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_budget_routes_make_and_move_the_legacy_folders(string id)
    {
        await app.ResetAsync();
        var golden = Golden.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var steps = GoldenScenarios.All[id];
        var goldenSteps = golden.GetProperty("steps").EnumerateArray().ToList();
        goldenSteps.Should().HaveCount(steps.Length);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var expectedTree = golden.GetProperty("nasFoldersAtEnd").EnumerateArray().Select(f => f.GetString()!).ToList();
        var countsBefore = (Budgets: 0, Tasks: 0);

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var expected = goldenSteps[i];
            app.Time.SetUtcNow(LisbonTime(expected.GetProperty("at").GetString()!));
            if (step.HideFolders)
            {
                await app.Nas.HideFoldersAsync(); // ssh-golden.js "nasFailsTestD"
            }

            var before = (await app.Nas.CommandsAsync()).Count;

            var (status, _, text) = await LegacyHttp.SendAsync(_client, step.Method, step.Url(refs), step.Body, null, Cancellation);
            var ops = (await app.Nas.CommandsAsync()).Skip(before).SelectMany(NasOp.FromCommand).ToList();

            var goldenOps = expected.GetProperty("events").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "ssh")
                .SelectMany(e => e.GetProperty("ops").EnumerateArray().Select(NasOp.FromGolden)).ToList();
            var (goldenStatus, goldenText) = (expected.GetProperty("response").GetProperty("status").GetInt32(), Text(expected));
            switch (step.Fix)
            {
                case Fix.None:
                    status.Should().Be(goldenStatus, $"{step.Label}: {text}");
                    if (goldenText is not null)
                    {
                        text.Should().Be(goldenText);
                    }

                    ops.Should().Equal(goldenOps, step.Label);
                    break;
                case Fix.D20QuotedCorrectly:
                    goldenStatus.Should().Be(500, "legacy: sh syntax error");
                    status.Should().Be(200, text);
                    ops.Should().Equal(BudgetFolderOps(step.ExpectedPath!));
                    expectedTree = [.. Ancestors(step.ExpectedPath!)];
                    break;
                case Fix.D21NoSlash:
                    (status, text).Should().Be((400, BudgetMessages.DescriptiveWithSlash));
                    ops.Should().BeEmpty();
                    expectedTree = [.. step.TreeAfterFix!];
                    break;
                case Fix.D6TaskKept:
                    (status, text).Should().Be((goldenStatus, goldenText!));
                    ops.Should().Equal(goldenOps);
                    break;
            }

            if (status == 200 && step.Ref is { } name)
            {
                refs[name] = JsonElement.Parse(text).GetProperty("_id").GetString()!;
            }

            if (status == 200 && goldenStatus == 200)
            {
                var fields = expected.GetProperty("response").GetProperty("fields");
                var answer = JsonElement.Parse(text);
                answer.GetProperty("budgetNumber").GetString().Should().Be(fields.GetProperty("budgetNumber").GetString());
                answer.GetProperty("descriptive").GetString().Should().Be(fields.GetProperty("descriptive").GetString());
            }

            var dbAfter = expected.GetProperty("dbAfter");
            (int Budgets, int Tasks) counts = step.Fix switch
            {
                Fix.D21NoSlash => countsBefore, // refused before anything is saved
                Fix.D6TaskKept or Fix.D20QuotedCorrectly => (dbAfter.GetProperty("budgets").GetInt32(), dbAfter.GetProperty("tasks").GetInt32() + 1),
                _ => (dbAfter.GetProperty("budgets").GetInt32(), dbAfter.GetProperty("tasks").GetInt32()),
            };
            ((int)await app.CountAsync("budgets"), (int)await app.CountAsync("tasks")).Should().Be(counts, $"{step.Label} (D6: the default task is saved before the NAS step)");
            countsBefore = counts;
        }

        (await app.Nas.FoldersAsync()).Except(["/share/Obras", "/share/Orçamentos"]).Should().Equal(expectedTree.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Each_ssh_step_is_in_the_log_store_as_nas_in_the_request()
    {
        await app.ResetAsync();
        app.Time.SetUtcNow(LisbonTime("2026-11-14T10:00:00 Europe/Lisbon"));

        var (status, _, _) = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Registo NAS"), null, Cancellation);

        status.Should().Be(200);
        var commands = await app.Nas.CommandsAsync();
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Count(x => x.Category == LogCategories.Nas && x.Message.Contains("Registo NAS", StringComparison.Ordinal)) >= 4, cancellationToken: Cancellation);
        var nas = rows.Where(r => r.Category == LogCategories.Nas && r.Message.Contains("Registo NAS", StringComparison.Ordinal)).ToList();
        nas.Should().HaveCount(4).And.AllSatisfy(r => (r.Level, r.UserName).Should().Be(("Information", TestUser.Admin.Name)));
        nas.Zip(commands).Should().AllSatisfy(pair => pair.First.Message.Should().StartWith($"NAS ran: {pair.Second} → exit 0 in "));
        var created = rows.Single(r => r.Category == LogCategories.App && r.Message.Contains("Registo NAS", StringComparison.Ordinal));
        rows.Where(r => r.RequestId == created.RequestId && r.Category == LogCategories.Nas).Should().HaveCountGreaterThanOrEqualTo(6, "connect, 4 commands, disconnect");
        rows.Should().NotContain(r => r.Message.Contains(NasFake.Password, StringComparison.Ordinal)
            || (r.PropertiesJson ?? "").Contains(NasFake.Password, StringComparison.Ordinal));
    }

    private static IEnumerable<NasOp> BudgetFolderOps(string path) =>
        [NasOp.MakeDirectory(path), NasOp.MakeDirectory(path + "/Geometria"), NasOp.MakeDirectory(path + "/Documentos"), NasOp.TestDirectory(path)];

    /// <summary>The folders a budget folder adds under <c>/share/Orçamentos</c>: year, client, folder, its two sub-folders.</summary>
    private static IEnumerable<string> Ancestors(string path)
    {
        var parts = path["/share/Orçamentos/".Length..].Split('/', 3);
        var year = "/share/Orçamentos/" + parts[0];
        return [year, year + "/" + parts[1], path, path + "/Documentos", path + "/Geometria"];
    }

    private static string? Text(JsonElement step)
    {
        var body = step.GetProperty("response").GetProperty("body").GetString();
        return body switch
        {
            "json" => null,
            "expressErrorPage" => LegacyMessages.SomethingFailed, // D16
            _ => body,
        };
    }

    private static DateTimeOffset LisbonTime(string at)
    {
        var local = DateTime.Parse(at.Replace(" Europe/Lisbon", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

/// <summary>
/// The NAS-on-Development check (PLAN.md §4.7, D5): outside Production nothing is sent to any NAS, the steps are
/// only logged ("would run", category <c>Nas</c>), and a rename of revision 0 saves (legacy tries SSH here, D5).
/// </summary>
public sealed class BudgetNasOffTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outside_production_the_folder_steps_are_only_logged_and_the_rename_saves_d5()
    {
        var client = app.CreateClient(TestUser.Admin);
        await GoldenScenarios.SeedAsync(app.Database);

        var created = await LegacyHttp.SendAsync(client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Sem NAS"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;
        var renamed = await LegacyHttp.SendAsync(client, HttpMethod.Put, "/api/budget/" + id, GoldenScenarios.Update("Sem NAS (novo)"), null, Cancellation);

        (created.Status, renamed.Status).Should().Be((200, 200));
        JsonElement.Parse(renamed.Text).GetProperty("descriptive").GetString().Should().Be("Sem NAS (novo)");
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Count(x => x.Message.Contains("would run", StringComparison.Ordinal)) >= 5, cancellationToken: Cancellation);
        var wouldRun = rows.Where(r => r.Message.Contains("would run", StringComparison.Ordinal)).Select(r => (r.Category, r.Message)).ToList();
        var number = JsonElement.Parse(created.Text).GetProperty("budgetNumber").GetString();
        var year = number![..2];
        var path = $"'/share/Orçamentos/20{year}/Metalúrgica Silva & Filhos, Lda./{number} - Sem NAS";
        wouldRun.Should().Equal(
            (LogCategories.Nas, $"NAS off (environment Development), would run: mkdir -p {path}'"),
            (LogCategories.Nas, $"NAS off (environment Development), would run: mkdir -p {path}/Geometria'"),
            (LogCategories.Nas, $"NAS off (environment Development), would run: mkdir -p {path}/Documentos'"),
            (LogCategories.Nas, $"NAS off (environment Development), would run: if test -d {path}'; then echo 1; else echo 0; fi"),
            (LogCategories.Nas, $"NAS off (environment Development), would run: mv {path}' {path} (novo)'"));
    }
}

/// <summary>The app in Production with SSH on, against its own fake NAS, with a settable clock.</summary>
public sealed class AppWithNas : AppWithMongo, IAsyncLifetime
{
    public NasFake Nas { get; } = new();

    public SettableTime Time { get; } = new();

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await Nas.InitializeAsync();
        await InitializeAsync();
        await GoldenScenarios.SeedAsync(Database);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync();
        await Nas.DisposeAsync();
    }

    /// <summary>No budgets, no tasks, an empty NAS.</summary>
    public async Task ResetAsync()
    {
        await Database.DropCollectionAsync("budgets", TestContext.Current.CancellationToken);
        await Database.DropCollectionAsync("tasks", TestContext.Current.CancellationToken);
        await Nas.ResetAsync();
    }

    public Task<long> CountAsync(string collection) =>
        Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production").UseSetting("Jwt:Key", "a-production-like-test-key-0123456789abcdef");
        foreach (var (key, value) in Nas.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}

/// <summary>A clock the scenarios set to each step's time (backwards too, unlike <c>FakeTimeProvider</c>).</summary>
public sealed class SettableTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void SetUtcNow(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>What the step deviates from legacy by (PLAN.md §9).</summary>
internal enum Fix
{
    None,

    /// <summary>D6: legacy leaves the budget without its default task when the folder is missing.</summary>
    D6TaskKept,

    /// <summary>D20: a <c>'</c> no longer breaks the command; the folder is made with the literal name.</summary>
    D20QuotedCorrectly,

    /// <summary>D21: a new or changed descriptive with <c>/</c> is a 400; nothing is sent to the NAS.</summary>
    D21NoSlash,
}

internal sealed record GoldenStep(string Label, HttpMethod Method, Func<IReadOnlyDictionary<string, string>, string> Url, string? Body, string? Ref)
{
    public Fix Fix { get; init; }

    /// <summary>The NAS answers 0 to the folder check from this step on.</summary>
    public bool HideFolders { get; init; }

    /// <summary>D20: the folder the step makes.</summary>
    public string? ExpectedPath { get; init; }

    /// <summary>D21: the folders at the end.</summary>
    public IReadOnlyList<string>? TreeAfterFix { get; init; }
}

/// <summary>
/// The budget scenarios of <c>tools/legacy/ssh-golden.js</c> (same clients, user-independent bodies), as requests.
/// The <c>*-dev</c> scenarios are covered by <see cref="BudgetNasOffTests"/>.
/// </summary>
internal static class GoldenScenarios
{
    /// <summary><c>CLIENTS</c> of <c>ssh-golden.js</c> (names byte for byte; <c>nfd</c> is non-NFC on purpose).</summary>
    public static readonly IReadOnlyDictionary<string, (ObjectId Id, string Name)> Clients = new Dictionary<string, (ObjectId, string)>(StringComparer.Ordinal)
    {
        ["silva"] = (ObjectId.Parse("000000000000000000000101"), "Metalúrgica Silva & Filhos, Lda."),
        ["apostrophe"] = (ObjectId.Parse("000000000000000000000102"), "Pastelaria D'Ouro"),
        ["nfd"] = (ObjectId.Parse("000000000000000000000103"), "Fundição Norte"),
        ["shellish"] = (ObjectId.Parse("000000000000000000000104"), "Ferragens $HOME `id` (Porto)"),
    };

    private const string Silva = "/share/Orçamentos/2026/Metalúrgica Silva & Filhos, Lda.";

    public static IReadOnlyDictionary<string, GoldenStep[]> All { get; } = new Dictionary<string, GoldenStep[]>(StringComparer.Ordinal)
    {
        ["budget-create"] = [CreateStep("b", "silva", "Máquina de corte")],
        ["budget-create-folder-missing"] = [CreateStep("b", "silva", "Máquina de corte") with { Fix = Fix.D6TaskKept, HideFolders = true }],
        ["budget-create-slash-in-descriptive"] = [CreateStep("b", "silva", "Suporte 30/40 mm") with { Fix = Fix.D21NoSlash, TreeAfterFix = [] }],
        ["budget-create-quotes-and-spaces"] = [CreateStep("b", "silva", "Molde \"especial\"  100% ")],
        ["budget-create-shell-chars-in-client"] = [CreateStep("b", "shellish", "Peça $1")],
        ["budget-create-non-nfc-client"] = [CreateStep("b", "nfd", "Caixa")],
        ["budget-create-apostrophe-in-client"] = [CreateStep("b", "apostrophe", "Vitrine refrigerada") with
        {
            Fix = Fix.D20QuotedCorrectly, ExpectedPath = "/share/Orçamentos/2026/Pastelaria D'Ouro/26-0001 - Vitrine refrigerada",
        }],
        ["budget-create-apostrophe-in-descriptive"] = [CreateStep("b", "silva", "Tampa O'Neill") with
        {
            Fix = Fix.D20QuotedCorrectly, ExpectedPath = Silva + "/26-0001 - Tampa O'Neill",
        }],
        ["budget-clone"] = [CreateStep("a", "silva", "Máquina de corte"), CloneStep("b", "a", "silva", "Máquina de corte v2")],
        ["budget-rename-rev0"] = [CreateStep("b", "silva", "Máquina de corte"), UpdateStep("b", "Máquina de corte CNC")],
        ["budget-rename-same-descriptive"] = [CreateStep("b", "silva", "Máquina de corte"), UpdateStep("b", "Máquina de corte")],
        ["budget-rename-revision"] = [CreateStep("b0", "silva", "Máquina de corte"), RevisionStep("b1", "b0"), UpdateStep("b1", "Máquina de corte (rev. 1)")],
        ["budget-rename-slash"] = [CreateStep("b", "silva", "Suporte"), UpdateStep("b", "Suporte 30/40") with
        {
            Fix = Fix.D21NoSlash,
            TreeAfterFix = ["/share/Orçamentos/2026", Silva, Silva + "/26-0001 - Suporte", Silva + "/26-0001 - Suporte/Documentos", Silva + "/26-0001 - Suporte/Geometria"],
        }],
    };

    public static string Create(string client, string descriptive) => JsonSerializer.Serialize(new
    {
        clientId = Clients[client].Id.ToString(),
        madeById = TestUser.Admin.Id.ToString(),
        descriptive,
        personInCharge = "Eng. Teste",
    });

    public static string Update(string descriptive) => JsonSerializer.Serialize(new { madeById = TestUser.Admin.Id.ToString(), descriptive, personInCharge = "Eng. Teste" });

    /// <summary>The clients of the scenarios (the prices come from the startup seed).</summary>
    public static async Task SeedAsync(IMongoDatabase database)
    {
        var entities = database.GetCollection<BsonDocument>("entities");
        foreach (var (id, name) in Clients.Values)
        {
            await entities.ReplaceOneAsync(new BsonDocument("_id", id),
                new BsonDocument { { "_id", id }, { "name", name }, { "type", "Cliente" }, { "paymentCondition", "30 dias" }, { "__v", 0 } },
                new ReplaceOptions { IsUpsert = true }, TestContext.Current.CancellationToken);
        }
    }

    private static GoldenStep CreateStep(string reference, string client, string descriptive) =>
        new($"POST /api/budget ({client}, \"{descriptive}\")", HttpMethod.Post, _ => "/api/budget", Create(client, descriptive), reference);

    private static GoldenStep CloneStep(string reference, string from, string client, string descriptive) =>
        new($"POST /api/budget/clone/{{{from}}}", HttpMethod.Post, refs => "/api/budget/clone/" + refs[from], Create(client, descriptive), reference);

    private static GoldenStep RevisionStep(string reference, string from) =>
        new($"POST /api/budget/newrevision/{{{from}}}", HttpMethod.Post, refs => "/api/budget/newrevision/" + refs[from], null, reference);

    private static GoldenStep UpdateStep(string target, string descriptive) =>
        new($"PUT /api/budget/{{{target}}} (descriptive \"{descriptive}\")", HttpMethod.Put, refs => "/api/budget/" + refs[target], Update(descriptive), null);
}
