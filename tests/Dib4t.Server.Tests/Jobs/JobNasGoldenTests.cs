using System.Text.Json;
using Dib4t.Application.Printing;
using Dib4t.Server.Tests.Budgets;
using Dib4t.Server.Tests.Settings;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Job create in <b>Production</b>, end to end (PLAN.md §5, §5.1): the app with SSH and label printing on, pointed at
/// the fake NAS and a fake printer (never the real ones), replays the job scenarios of
/// <c>tests/fixtures/legacy/ssh/ssh-commands.json</c> (<c>tools/legacy/ssh-golden.js</c>: same requests, same clock).
/// Where legacy works, the new app sends the same <c>ops</c> in the same order, leaves the same folder tree and sends
/// the same label bytes (producao, then maquinacao), except D4: the two checks test the <b>absolute</b> job folders
/// (legacy: paths relative to the SSH home, never awaited). D3 and D19 (legacy 500 after the job is saved) now move the
/// folders of revision 0 and print the labels.
/// </summary>
public sealed class JobNasGoldenTests(AppWithNas app) : IClassFixture<AppWithNas>, IAsyncLifetime
{
    private const string Silva = "Metalúrgica Silva & Filhos, Lda.";

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Path.Combine(Fixtures.Root, "legacy", "ssh", "ssh-commands.json")));

    private readonly HttpClient _client = app.CreateClient(TestUser.Admin);
    private readonly PrinterFake _printer = new();

    /// <summary>What the scenario's last step (the job) deviates from legacy by.</summary>
    public enum JobFix
    {
        /// <summary>Legacy works: same ops (D4: the checks on absolute paths), same tree, same label bytes.</summary>
        D4AbsoluteChecks,

        /// <summary>D3: a revision with another descriptive than revision 0 (legacy ReferenceError, 500, nothing moved).</summary>
        D3FromRevisionZero,

        /// <summary>D19: a revision dated in a later year than revision 0 (legacy: wrong source year, mv fails, 500).</summary>
        D19FromRevisionZero,
    }

    public static TheoryData<string> Scenarios => [.. Steps.Keys];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The job scenarios of <c>ssh-golden.js</c> (<c>job-create-dev</c>: <see cref="JobNasOffTests"/>).</summary>
    private static Dictionary<string, (Func<GoldenStep>[] Budget, string Job, JobFix Fix)> Steps { get; } = new(StringComparer.Ordinal)
    {
        ["job-create-rev0"] = ([() => Create("b", "Máquina de corte")], "b", JobFix.D4AbsoluteChecks),
        ["job-create-rev1-same-descriptive"] = ([() => Create("b0", "Máquina de corte"), () => Revision("b1", "b0")], "b1", JobFix.D4AbsoluteChecks),
        ["job-create-rev1-renamed"] = ([() => Create("b0", "Máquina de corte"), () => Revision("b1", "b0"), () => Update("b1", "Máquina de corte (rev. 1)")],
            "b1", JobFix.D3FromRevisionZero),
        ["job-create-after-rev0-rename"] = ([() => Create("b", "Máquina de corte"), () => Update("b", "Máquina de corte CNC")], "b", JobFix.D4AbsoluteChecks),
        ["job-create-rev1-later-year"] = ([() => Create("b0", "Máquina de corte"), () => Revision("b1", "b0")], "b1", JobFix.D19FromRevisionZero),
        // A "/" can no longer be typed (D21): the budget is created, then given the legacy descriptive and folders directly.
        ["job-create-slash-in-descriptive"] = ([() => Create("b", "Suporte 30")], "b", JobFix.D4AbsoluteChecks),
    };

    public async ValueTask InitializeAsync()
    {
        await app.ResetAsync();
        await app.Database.DropCollectionAsync("jobs", Cancellation);
        var printers = app.Database.GetCollection<BsonDocument>("printersettings");
        await app.Database.DropCollectionAsync("printersettings", Cancellation);
        // ssh-golden.js: one producao printer, then one maquinacao printer (here both on the fake, so the order shows).
        foreach (var template in new[] { "producao", "maquinacao" })
        {
            await printers.InsertOneAsync(new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "ip", "127.0.0.1" }, { "port", _printer.Port }, { "templateName", template }, { "__v", 0 },
            }, cancellationToken: Cancellation);
        }
    }

    public ValueTask DisposeAsync() => _printer.DisposeAsync();

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Job_create_moves_the_budget_folder_and_prints_the_labels(string id)
    {
        var golden = Golden.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var goldenSteps = golden.GetProperty("steps").EnumerateArray().ToList();
        var (budgetSteps, jobRef, fix) = Steps[id];
        goldenSteps.Should().HaveCount(budgetSteps.Length + 1);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < budgetSteps.Length; i++)
        {
            var step = budgetSteps[i]();
            app.Time.SetUtcNow(LisbonTime(goldenSteps[i].GetProperty("at").GetString()!));
            var (status, _, text) = await LegacyHttp.SendAsync(_client, step.Method, step.Url(refs), step.Body, null, Cancellation);
            status.Should().Be(200, $"{step.Label}: {text}");
            if (step.Ref is { } name)
            {
                refs[name] = JsonElement.Parse(text).GetProperty("_id").GetString()!;
            }
        }

        if (id == "job-create-slash-in-descriptive")
        {
            await GiveLegacySlashDescriptiveAsync(refs["b"]);
        }

        var jobStep = goldenSteps[^1];
        app.Time.SetUtcNow(LisbonTime(jobStep.GetProperty("at").GetString()!));
        var before = (await app.Nas.CommandsAsync()).Count;
        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", JobBody(refs[jobRef]), null, Cancellation);

        var ops = (await app.Nas.CommandsAsync()).Skip(before).SelectMany(NasOp.FromCommand).ToList();
        var events = jobStep.GetProperty("events").EnumerateArray().ToList();
        var goldenOps = events.Where(e => e.GetProperty("type").GetString() == "ssh").SelectMany(e => e.GetProperty("ops").EnumerateArray().Select(NasOp.FromGolden)).ToList();
        var budget = JsonElement.Parse(answer.Text).GetProperty("budget");
        var (number, descriptive) = (budget.GetProperty("budgetNumber").GetString()!, budget.GetProperty("descriptive").GetString()!);
        var revisionZero = await app.Database.GetCollection<BsonDocument>("budgets").Find(new BsonDocument { { "budgetNumber", number }, { "revision", 0 } }).SingleAsync(Cancellation);
        var source = $"/share/Orçamentos/{LisbonYear(revisionZero["date"].ToUniversalTime())}/{Silva}/{number} - {revisionZero["descriptive"].AsString}";
        var budgetYear = LisbonYear(budget.GetProperty("date").GetDateTime().ToUniversalTime());
        var target = $"/share/Obras/{budgetYear}/{Silva}/{number} - {descriptive}";
        var expectedOps = MoveOps(source, target);
        var expectedTree = Tree(source, target);

        answer.Status.Should().Be(200, answer.Text);
        switch (fix)
        {
            case JobFix.D4AbsoluteChecks:
                jobStep.GetProperty("response").GetProperty("status").GetInt32().Should().Be(200);
                goldenOps.Select(D4Absolute).Should().Equal(expectedOps, "legacy's ops but the checks, on the absolute job folders");
                golden.GetProperty("nasFoldersAtEnd").EnumerateArray().Select(f => f.GetString()!).Should().Equal(expectedTree);
                break;
            case JobFix.D3FromRevisionZero:
                (jobStep.GetProperty("response").GetProperty("status").GetInt32(), goldenOps.Count).Should().Be((500, 0), "legacy: ReferenceError, nothing moved");
                break;
            case JobFix.D19FromRevisionZero:
                jobStep.GetProperty("response").GetProperty("status").GetInt32().Should().Be(500, "legacy: mv from the revision's year fails");
                source.Should().Contain("/2025/").And.NotBe(goldenOps.Single(o => o.Op == "mv").From![..^"/Documentos".Length]);
                break;
        }

        ops.Should().Equal(expectedOps, id);
        (await app.Nas.FoldersAsync()).Except(["/share/Obras", "/share/Orçamentos"]).Should().Equal(expectedTree, id);

        var labels = await _printer.WaitForAsync(2);
        var expectedLabels = fix == JobFix.D4AbsoluteChecks
            ? events.Where(e => e.GetProperty("type").GetString() == "printer").Select(e => e.GetProperty("wireHex").GetString()!).ToList()
            : [.. new[] { "producao", "maquinacao" }.Select(t => Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(
                LabelTemplates.Payload(t, number, descriptive, Silva, DateOnly.Parse(jobStep.GetProperty("at").GetString()![..10], System.Globalization.CultureInfo.InvariantCulture)) + "\n")))];
        labels.Select(Convert.ToHexStringLower).Should().Equal(expectedLabels, "UTF8(template + \"\\n\"), producao then maquinacao");
        (await app.CountAsync("jobs")).Should().Be(1);
    }

    [Fact]
    public async Task A_missing_budget_folder_is_the_legacy_500_after_the_job_is_saved_and_no_label_is_printed()
    {
        app.Time.SetUtcNow(LisbonTime("2026-11-02T10:00:00 Europe/Lisbon"));
        var created = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Sem pasta"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;
        await app.Nas.ResetAsync(); // someone removed the budget folder on the NAS

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", JobBody(id), null, Cancellation);

        (answer.Status, answer.Text).Should().Be((500, "Something failed."));
        (await app.CountAsync("jobs")).Should().Be(1, "as legacy, the job is saved before the NAS step");
        (await app.Nas.CommandsAsync()).Select(NasOp.FromCommand).SelectMany(o => o).Select(o => o.Op).Should().Equal("mkdir-p", "mkdir-p", "mkdir-p", "mv");
        await Task.Delay(300, Cancellation);
        _printer.Received.Should().BeEmpty("legacy prints nothing after a failed move");
    }

    [Fact]
    public async Task D4_a_folder_missing_after_the_move_is_a_warning_and_the_job_is_created()
    {
        app.Time.SetUtcNow(LisbonTime("2026-11-02T10:00:00 Europe/Lisbon"));
        var created = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Verificação D4"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;
        await app.Nas.HideFoldersAsync(); // every test -d answers 0

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", JobBody(id), null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        (await _printer.WaitForAsync(2)).Should().HaveCount(2);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Count(x => x.Message.StartsWith("NAS folder not there after the move", StringComparison.Ordinal)) >= 2, cancellationToken: Cancellation);
        rows.Where(r => r.Message.StartsWith("NAS folder not there after the move", StringComparison.Ordinal))
            .Should().HaveCount(2).And.AllSatisfy(r => (r.Level, r.Category).Should().Be(("Warning", LogCategories.Nas)));
    }

    [Fact]
    public async Task Each_label_is_in_the_log_store_as_printer_in_the_request()
    {
        app.Time.SetUtcNow(LisbonTime("2026-11-02T10:00:00 Europe/Lisbon"));
        var created = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Registo etiquetas"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", JobBody(id), null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        var port = $"Printer 127.0.0.1:{_printer.Port}:"; // this test's fake (the log store is shared by the class)
        bool IsLabel(LogRow r) => r.Category == LogCategories.Printer && r.Message.StartsWith(port, StringComparison.Ordinal);
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath, r => r.Count(IsLabel) >= 2, cancellationToken: Cancellation);
        var printed = rows.Where(IsLabel).ToList();
        printed.Select(r => r.Message).Should().SatisfyRespectively(
            first => first.Should().StartWith($"Printer 127.0.0.1:{_printer.Port}: label producao sent, "),
            second => second.Should().StartWith($"Printer 127.0.0.1:{_printer.Port}: label maquinacao sent, "));
        var job = rows.Single(r => r.Category == LogCategories.App && r.Message.StartsWith("Job ", StringComparison.Ordinal) && r.RequestId == printed[0].RequestId);
        job.Message.Should().Contain(JsonElement.Parse(answer.Text).GetProperty("_id").GetString());
        printed.Should().AllSatisfy(r => (r.RequestId, r.UserName).Should().Be((job.RequestId, TestUser.Admin.Name)));
    }

    [Fact]
    public async Task An_unreachable_printer_is_logged_and_the_job_is_created()
    {
        app.Time.SetUtcNow(LisbonTime("2026-11-02T10:00:00 Europe/Lisbon"));
        await app.Database.GetCollection<BsonDocument>("printersettings").InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "ip", "127.0.0.1" }, { "port", FreePort() }, { "templateName", "producao" }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        var created = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Impressora desligada"), null, Cancellation);
        var id = JsonElement.Parse(created.Text).GetProperty("_id").GetString()!;

        var answer = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/job", JobBody(id), null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        (await _printer.WaitForAsync(2)).Should().HaveCount(2, "the other printers still get their labels");
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Category == LogCategories.Printer && x.Level == "Error"), cancellationToken: Cancellation);
        rows.Should().Contain(r => r.Category == LogCategories.Printer && r.Level == "Error" && r.Message.Contains("label producao not sent", StringComparison.Ordinal));
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The legacy shape of a budget made before D21: a <c>/</c> in the descriptive and its nested folders.</summary>
    private async Task GiveLegacySlashDescriptiveAsync(string id)
    {
        await app.Database.GetCollection<BsonDocument>("budgets").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)),
            new BsonDocument("$set", new BsonDocument("descriptive", "Suporte 30/40 mm")), cancellationToken: Cancellation);
        await app.Nas.ResetAsync();
        var folder = $"/share/Orçamentos/2026/{Silva}/26-0001 - Suporte 30/40 mm";
        await app.Nas.MakeFolderAsync(folder + "/Geometria");
        await app.Nas.MakeFolderAsync(folder + "/Documentos");
    }

    /// <summary>The body of <c>ssh-golden.js</c> <c>createJob</c>.</summary>
    private static string JobBody(string budgetId) => JsonSerializer.Serialize(new
    {
        budgetId,
        finalPrice = 1000,
        dueDate = "2027-02-01",
        state = "Desenvolvimento",
        orderIdentification = "PO-1",
        isOfficialOrderIdentification = true,
    });

    private static NasOp D4Absolute(NasOp op) =>
        op.Op == "test-d" ? op with { Path = op.Path!.Replace(NasFake.Home + "/share/", "/share/", StringComparison.Ordinal) } : op;

    private static List<NasOp> MoveOps(string source, string target) =>
    [
        NasOp.MakeDirectory(target + "/CAD"), NasOp.MakeDirectory(target + "/CAD/Equipamento"), NasOp.MakeDirectory(target + "/CAM"),
        NasOp.Move(source + "/Documentos", target + "/Informações Técnicas"), NasOp.Move(source + "/Geometria", target + "/CAD/Artigo"),
        NasOp.TestDirectory(target + "/Informações Técnicas"), NasOp.TestDirectory(target + "/CAD/Artigo"),
    ];

    /// <summary>The folders after the move: the budget folder (emptied) and its parents, the job folder with its sub-folders.</summary>
    private static List<string> Tree(string source, string target)
    {
        static IEnumerable<string> Parents(string path, string root)
        {
            var parts = path[(root.Length + 1)..].Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                yield return root + "/" + string.Join('/', parts[..i]);
            }
        }

        return
        [
            .. Parents(source, "/share/Orçamentos")
                .Concat(Parents(target, "/share/Obras"))
                .Concat(new[] { "CAD", "CAD/Artigo", "CAD/Equipamento", "CAM", "Informações Técnicas" }.Select(sub => target + "/" + sub))
                .Distinct().Order(StringComparer.Ordinal),
        ];
    }

    private static GoldenStep Create(string reference, string descriptive) =>
        new($"POST /api/budget (silva, \"{descriptive}\")", HttpMethod.Post, _ => "/api/budget", GoldenScenarios.Create("silva", descriptive), reference);

    private static GoldenStep Revision(string reference, string from) =>
        new($"POST /api/budget/newrevision/{{{from}}}", HttpMethod.Post, refs => "/api/budget/newrevision/" + refs[from], null, reference);

    private static GoldenStep Update(string target, string descriptive) =>
        new($"PUT /api/budget/{{{target}}}", HttpMethod.Put, refs => "/api/budget/" + refs[target], GoldenScenarios.Update(descriptive), null);

    private static int LisbonYear(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon")).Year;

    private static DateTimeOffset LisbonTime(string at)
    {
        var local = DateTime.Parse(at.Replace(" Europe/Lisbon", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

/// <summary>
/// Job create outside Production (PLAN.md §4.7, golden scenario <c>job-create-dev</c>): no NAS, no printer; the commands
/// and the labels are only logged ("would run", "would send") with the very texts the adapters would send.
/// </summary>
public sealed class JobNasOffTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outside_production_the_folder_move_and_the_labels_are_only_logged()
    {
        var client = app.CreateClient(TestUser.Admin);
        await GoldenScenarios.SeedAsync(app.Database);
        await app.Database.GetCollection<BsonDocument>("printersettings").InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "ip", "192.168.2.32" }, { "port", 9100 }, { "templateName", "producao" }, { "__v", 0 },
        }, cancellationToken: Cancellation);
        var created = await LegacyHttp.SendAsync(client, HttpMethod.Post, "/api/budget", GoldenScenarios.Create("silva", "Obra sem NAS"), null, Cancellation);
        var budget = JsonElement.Parse(created.Text);

        var answer = await LegacyHttp.SendAsync(client, HttpMethod.Post, "/api/job", JsonSerializer.Serialize(new
        {
            budgetId = budget.GetProperty("_id").GetString(),
            finalPrice = 1000,
            dueDate = "2027-02-01",
            state = "Desenvolvimento",
            orderIdentification = "PO-1",
            isOfficialOrderIdentification = true,
        }), null, Cancellation);

        answer.Status.Should().Be(200, answer.Text);
        var number = budget.GetProperty("budgetNumber").GetString()!;
        var year = "20" + number[..2];
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Any(x => x.Message.StartsWith("Printer off", StringComparison.Ordinal)), cancellationToken: Cancellation);
        var source = $"/share/Orçamentos/{year}/Metalúrgica Silva & Filhos, Lda./{number} - Obra sem NAS";
        var target = $"/share/Obras/{year}/Metalúrgica Silva & Filhos, Lda./{number} - Obra sem NAS";
        rows.Where(r => r.Category == LogCategories.Nas && r.Message.Contains("Obras", StringComparison.Ordinal)).Select(r => r.Message).Should().Equal(
            $"NAS off (environment Development), would run: mkdir -p '{target}/CAD'",
            $"NAS off (environment Development), would run: mkdir -p '{target}/CAD/Equipamento'",
            $"NAS off (environment Development), would run: mkdir -p '{target}/CAM'",
            $"NAS off (environment Development), would run: mv '{source}/Documentos' '{target}/Informações Técnicas'",
            $"NAS off (environment Development), would run: mv '{source}/Geometria' '{target}/CAD/Artigo'",
            $"NAS off (environment Development), would run: if test -d '{target}/Informações Técnicas'; then echo 1; else echo 0; fi",
            $"NAS off (environment Development), would run: if test -d '{target}/CAD/Artigo'; then echo 1; else echo 0; fi");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon")));
        var payload = LabelTemplates.Payload("producao", number, "Obra sem NAS", "Metalúrgica Silva & Filhos, Lda.", today);
        payload.Should().StartWith("\n      \u001bia3,^TS001" + number + ",").And.Contain(",Obra sem NAS,Metalúrgica Silva & Filhos, Lda.,");
        rows.Where(r => r.Category == LogCategories.Printer && r.Message.StartsWith("Printer ", StringComparison.Ordinal)).Select(r => r.Message).Should().Equal(
            $"Printer off (environment Development), would send to 192.168.2.32:9100 label producao, {System.Text.Encoding.UTF8.GetByteCount(payload + "\n")} bytes: "
            + payload.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\u001b", "\\u001b", StringComparison.Ordinal));
    }
}
