using System.Net;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 4 step 4 (PLAN.md §4.0 item 3, §11.3) for the label print: after the production backup is imported, "Imprimir"
/// (both templates) is asked for <b>every</b> budget revision marked as having a job. The app runs in Production with
/// <c>LabelPrinter:Enabled=false</c>: the backup holds the real printers' addresses, so the labels are only logged,
/// never sent (§5.1, §4.7). Each revision whose job exists gets "ok" and two logged labels with its job's start date
/// (no job of the backup has a <c>null</c> start or a missing client, so none is the legacy 500; all 1117 marked
/// revisions have their job, 2 printers → 2234 labels); a revision whose job was made from another revision would be
/// the legacy "Obra encontrada não encontrado". Nothing in the database changes.
/// </summary>
public sealed class ImportedJobPrintTests(AppWithLoggedPrinters app) : IClassFixture<AppWithLoggedPrinters>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_job_prints_both_labels_and_nothing_changes()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        var jobBudgets = seed.Collections["jobs"].Where(j => j.Contains("budget")).Select(j => j["budget"].AsObjectId).ToHashSet();
        var marked = seed.Collections["budgets"].Where(b => b.GetValue("hasGenerateJob", false).AsBoolean).ToList();
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        var printed = 0;
        foreach (var budget in marked)
        {
            using var response = await client.PostAsync(new Uri("/api/job/print/" + budget["_id"], UriKind.Relative),
                new StringContent("""{"maquinacao":true,"producao":true}""", System.Text.Encoding.UTF8, "application/json"), Cancellation);
            var text = await response.Content.ReadAsStringAsync(Cancellation);
            statuses[$"{(int)response.StatusCode} {text}"] = statuses.GetValueOrDefault($"{(int)response.StatusCode} {text}") + 1;
            if (jobBudgets.Contains(budget["_id"].AsObjectId))
            {
                (response.StatusCode, text).Should().Be((HttpStatusCode.OK, "ok"), budget["budgetNumber"].AsString);
                printed++;
            }
            else
            {
                (response.StatusCode, text).Should().Be((HttpStatusCode.BadRequest, "Obra encontrada não encontrado"), budget["budgetNumber"].AsString);
            }
        }

        var summary = string.Join(", ", statuses.Select(s => $"{s.Key}: {s.Value}"));
        (printed, marked.Count).Should().Be((1117, 1117), "every marked revision of the backup has its job: " + summary);
        var printers = seed.Collections["printersettings"];
        var expectedLabels = printed * printers.Count;
        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath,
            r => r.Count(x => x.Category == LogCategories.Printer && x.Message.StartsWith("Printer off", StringComparison.Ordinal)) >= expectedLabels,
            cancellationToken: Cancellation);
        rows.Where(r => r.Category == LogCategories.Printer && r.Message.StartsWith("Printer ", StringComparison.Ordinal))
            .Should().AllSatisfy(r => r.Message.Should().StartWith("Printer off (LabelPrinter:Enabled=false), would send to ",
                "nothing is sent to (or tried on) the real printers of the backup: no line of the TCP adapter"));
        rows.Count(r => r.Message.StartsWith("Printer off", StringComparison.Ordinal)).Should().Be(expectedLabels, summary);

        // users: the signed-in test user is saved before each request (TestUserHandler); the imported users are checked below
        foreach (var (name, documents) in seed.Collections)
        {
            if (name == "users")
            {
                var users = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
                users.Select(d => d.ToJson()).Should().Contain(documents.Select(d => d.ToJson())).And.HaveCount(documents.Count + 1);
                continue;
            }

            var after = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            after.Select(d => d.ToJson()).Should().BeEquivalentTo(documents.Select(d => d.ToJson()), name + " is not touched");
        }
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}

/// <summary>The app in Production with the NAS and the label printers off (labels only logged, PLAN.md §4.7).</summary>
public sealed class AppWithLoggedPrinters : AppWithMongo
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Production")
            .UseSetting("Jwt:Key", "a-production-like-test-key-0123456789abcdef")
            .UseSetting("Nas:Enabled", "false")
            .UseSetting("LabelPrinter:Enabled", "false");
}
