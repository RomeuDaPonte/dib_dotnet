using System.Text.Json;
using Dib4t.Server.Tests.Budgets;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace Dib4t.Server.Tests.Jobs;

/// <summary>
/// Every Production case of <c>job-print-golden.json</c> (<c>POST print/:budgetId</c>, the legacy handler and
/// <c>PrinterSettings.print</c> run verbatim; <see cref="JobGoldenReplay"/>) over HTTP on mongo:9.0.8, in the
/// <b>Production</b> environment with label printing on, every printer of the case pointed at an in-test TCP listener
/// (PLAN.md §5.1: never a real printer): the same status and text, no job changed, and exactly the golden wire bytes
/// in the golden order (producao printers, then maquinacao printers); the unreachable printer of the golden file is a
/// closed local port (logged, the others still printed). Expected outcomes are the D23 ones (<c>fixed</c>: the
/// Europe/Lisbon date).
/// </summary>
public sealed class JobPrintGoldenTests(AppWithPrinters app)
    : JobGoldenReplay(app, PrintGolden), IClassFixture<AppWithPrinters>, IAsyncDisposable
{
    internal static readonly JsonElement PrintGolden = JsonElement.Parse(File.ReadAllText(Fixtures.JobPrintGolden));

    private readonly AppWithPrinters _app = app;
    private readonly PrinterFake _printer = new();

    public static TheoryData<int> ProductionCases => Indexes(c => c.GetProperty("nodeEnv").GetString() == "prod");

    [Theory]
    [MemberData(nameof(ProductionCases))]
    public async Task Each_legacy_case_gives_the_same_answer_and_label_bytes(int index)
    {
        var item = PrintGolden.GetProperty("cases")[index];
        await SetPrintersAsync(_app, item.GetProperty("printers").GetString()!, _printer.Port);

        await ReplayAsync(index);

        var expected = SentLabels(ExpectedOutcome(item));
        await ShouldReceiveAsync(_printer, expected, $"#{index} {item.GetProperty("path").GetString()} {item.GetProperty("body").GetString()}");
    }

    [Fact]
    public void D23_changes_only_the_late_summer_start()
    {
        var changed = PrintGolden.GetProperty("cases").EnumerateArray()
            .Where(c => c.TryGetProperty("fixed", out var f) && f.GetRawText() != c.GetProperty("legacy").GetRawText()).ToList();
        changed.Select(c => c.GetProperty("path").GetString()).Should().Equal($"/api/job/print/{{{{budget-late-start}}}}");
        var (legacy, fixedOutcome) = (SentLabels(changed[0].GetProperty("legacy")), SentLabels(changed[0].GetProperty("fixed")));
        legacy.Select(Text).Should().AllSatisfy(t => t.Should().Contain(",31/7/2026,"), "legacy: the UTC date");
        fixedOutcome.Select(Text).Should().AllSatisfy(t => t.Should().Contain(",1/8/2026,"), "D23: the Lisbon date");

        static string Text(string hex) => System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex));
    }

    /// <summary>DoD 6 (§3.3): the use-case event and each label are in the log store, under the request, with the user.</summary>
    [Fact]
    public async Task The_print_and_each_label_are_logged_with_the_request_and_the_user()
    {
        var index = Index(c => c.GetProperty("section").GetString() == "print" && c.GetProperty("printers").GetString() == "both"
            && c.GetProperty("path").GetString()!.EndsWith("{{budget-dev}}", StringComparison.Ordinal) && c.GetProperty("body").GetString()!.Contains("\"maquinacao\":true,\"producao\":true", StringComparison.Ordinal));
        await SetPrintersAsync(_app, "both", _printer.Port);

        await ReplayAsync(index);

        await _printer.WaitForAsync(2);
        var port = $"Printer 127.0.0.1:{_printer.Port}:";
        bool IsLabel(LogRow r) => r.Message.StartsWith(port, StringComparison.Ordinal); // this test's fake (the store is shared by the class)
        var rows = await LogStoreFile.WaitForAsync(_app.LogStorePath,
            r => r.Count(IsLabel) >= 2 && r.Any(x => x.Message.StartsWith("Labels of budget", StringComparison.Ordinal) && x.RequestId == r.First(IsLabel).RequestId),
            cancellationToken: Cancellation);
        var labels = rows.Where(IsLabel).ToList();
        labels.Select(r => r.Message).Should().SatisfyRespectively(
            first => first.Should().StartWith(port + " label producao sent, "),
            second => second.Should().StartWith(port + " label maquinacao sent, "));
        var printed = rows.Single(r => r.Message.StartsWith("Labels of budget", StringComparison.Ordinal) && r.RequestId == labels[0].RequestId);
        printed.Message.Should().Contain("2 sent");
        (printed.Level, printed.Category, printed.UserName).Should().Be(("Information", LogCategories.App, TestUser.Admin.Name));
        printed.RequestId.Should().NotBeNullOrEmpty();
        labels.Should().AllSatisfy(r => (r.Category, r.RequestId, r.UserName).Should().Be((LogCategories.Printer, printed.RequestId, TestUser.Admin.Name)));
    }

    public ValueTask DisposeAsync() => _printer.DisposeAsync();

    /// <summary>The wire bytes (hex) of the labels the legacy code sent (a refused connection sends nothing).</summary>
    internal static List<string> SentLabels(JsonElement outcome) =>
        [.. outcome.GetProperty("labels").EnumerateArray().Where(l => l.TryGetProperty("send", out _)).Select(l => l.GetProperty("wireHex").GetString()!)];

    /// <summary>The printers of a golden printer set, in order: the reachable ones on the fake, the unreachable one on a closed port.</summary>
    internal static async Task SetPrintersAsync(AppWithMongo app, string set, int fakePort)
    {
        var collection = app.Database.GetCollection<BsonDocument>("printersettings");
        await app.Database.DropCollectionAsync("printersettings", Cancellation);
        var unreachable = PrintGolden.GetProperty("unreachablePrinter").GetString();
        var closedPort = ClosedPort();
        foreach (var printer in PrintGolden.GetProperty("printerSets").GetProperty(set).EnumerateArray())
        {
            await collection.InsertOneAsync(new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "ip", "127.0.0.1" },
                { "port", printer.GetProperty("ip").GetString() == unreachable ? closedPort : fakePort },
                { "templateName", printer.GetProperty("templateName").GetString() },
                { "__v", 0 },
            }, cancellationToken: Cancellation);
        }
    }

    internal static async Task ShouldReceiveAsync(PrinterFake printer, IReadOnlyList<string> expected, string because)
    {
        IReadOnlyList<byte[]> received;
        if (expected.Count == 0)
        {
            await Task.Delay(200, Cancellation);
            received = printer.Received;
        }
        else
        {
            received = await printer.WaitForAsync(expected.Count);
        }

        received.Select(Convert.ToHexStringLower).Should().Equal(expected, "labels of " + because);
    }

    private static TheoryData<int> Indexes(Func<JsonElement, bool> where)
    {
        var data = new TheoryData<int>();
        var cases = PrintGolden.GetProperty("cases");
        for (var i = 0; i < cases.GetArrayLength(); i++)
        {
            if (where(cases[i]))
            {
                data.Add(i);
            }
        }

        return data;
    }

    private static int Index(Func<JsonElement, bool> where) =>
        PrintGolden.GetProperty("cases").EnumerateArray().Select((c, i) => (c, i)).First(x => where(x.c)).i;

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// The development guard of <c>job-print-golden.json</c>: outside Production every request is the legacy 400
/// "Só funciona em produção!" before anything else (an invalid id or body too), and no printer is contacted.
/// </summary>
public sealed class JobPrintDevelopmentGoldenTests(AppWithGoldenClock app)
    : JobGoldenReplay(app, JobPrintGoldenTests.PrintGolden), IClassFixture<AppWithGoldenClock>, IAsyncDisposable
{
    private readonly AppWithGoldenClock _app = app;
    private readonly PrinterFake _printer = new();

    public static TheoryData<int> DevelopmentCases
    {
        get
        {
            var data = new TheoryData<int>();
            var cases = JobPrintGoldenTests.PrintGolden.GetProperty("cases");
            for (var i = 0; i < cases.GetArrayLength(); i++)
            {
                if (cases[i].GetProperty("nodeEnv").ValueKind == JsonValueKind.Null)
                {
                    data.Add(i);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DevelopmentCases))]
    public async Task Outside_production_the_route_is_the_legacy_400_and_prints_nothing(int index)
    {
        await JobPrintGoldenTests.SetPrintersAsync(_app, "both", _printer.Port);

        await ReplayAsync(index);

        await JobPrintGoldenTests.ShouldReceiveAsync(_printer, [], $"#{index}");
    }

    public ValueTask DisposeAsync() => _printer.DisposeAsync();
}

/// <summary>
/// Production with <c>LabelPrinter:Enabled=false</c> (the pilot, PLAN.md §4.7): the same answers as with the printers
/// on, and the labels are only logged ("would send"), never sent.
/// </summary>
public sealed class JobPrintPrintersOffTests(AppWithPrintersOff app) : IClassFixture<AppWithPrintersOff>, IAsyncDisposable
{
    private readonly PrinterFake _printer = new();

    [Fact]
    public async Task The_labels_are_logged_and_not_sent()
    {
        await JobPrintGoldenTests.SetPrintersAsync(app, "both", _printer.Port);
        var replay = new Replay(app);
        var index = JobPrintGoldenTests.PrintGolden.GetProperty("cases").EnumerateArray().Select((c, i) => (c, i))
            .First(x => x.c.GetProperty("section").GetString() == "print" && JobPrintGoldenTests.SentLabels(x.c.GetProperty("fixed")).Count == 2).i;

        await replay.RunAsync(index);

        await JobPrintGoldenTests.ShouldReceiveAsync(_printer, [], "LabelPrinter:Enabled=false");
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Count(x => x.Category == LogCategories.Printer && x.Message.Contains("would send", StringComparison.Ordinal)) >= 2, cancellationToken: TestContext.Current.CancellationToken);
        rows.Should().Contain(r => r.Message.StartsWith("Labels of budget", StringComparison.Ordinal));
    }

    public ValueTask DisposeAsync() => _printer.DisposeAsync();

    private sealed class Replay(AppWithMongo app) : JobGoldenReplay(app, JobPrintGoldenTests.PrintGolden)
    {
        public Task RunAsync(int index) => ReplayAsync(index);
    }
}

/// <summary>The app in <b>Production</b> with label printing over TCP (the NAS off), on the golden files' clock.</summary>
public class AppWithPrinters : AppWithMongo
{
    public SettableTime Time { get; } = CreateTime();

    /// <summary><c>LabelPrinter:Enabled</c> (default on in Production).</summary>
    protected virtual bool PrintersEnabled => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production")
            .UseSetting("Jwt:Key", "a-production-like-test-key-0123456789abcdef")
            .UseSetting("Nas:Enabled", "false")
            .UseSetting("LabelPrinter:Enabled", PrintersEnabled ? "true" : "false");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    private static SettableTime CreateTime()
    {
        var time = new SettableTime();
        time.SetUtcNow(DateTimeOffset.Parse("2026-03-15T10:20:30.123Z", System.Globalization.CultureInfo.InvariantCulture));
        return time;
    }
}

/// <summary>Production with <c>LabelPrinter:Enabled=false</c>.</summary>
public sealed class AppWithPrintersOff : AppWithPrinters
{
    protected override bool PrintersEnabled => false;
}
