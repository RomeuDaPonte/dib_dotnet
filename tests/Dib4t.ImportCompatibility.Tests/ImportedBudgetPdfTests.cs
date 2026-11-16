using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 3 step 3 (PLAN.md §4.0 item 3, §11.3) for the PDF: after the production backup is imported, every budget
/// (all revisions, locked or not, with or without a job) gets its PDF, and the only change in the whole database is
/// <c>wasPdfDownloaded: true</c> on the budgets that did not have it (written in place, BSON types and field order
/// kept). The layout of every one of these budgets is checked against the legacy pdfmake in
/// <c>PdfLayoutGoldenTests</c> (Infrastructure.Tests).
/// </summary>
public sealed partial class ImportedBudgetPdfTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_imported_budget_gets_its_PDF_and_only_the_flag_changes()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        var stored = seed.Collections["budgets"];
        var pages = new List<int>();
        foreach (var document in stored)
        {
            using var response = await client.GetAsync(new Uri("/api/budget/getPdf/" + document["_id"], UriKind.Relative), Cancellation);
            var text = await response.Content.ReadAsStringAsync(Cancellation);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{document["budgetNumber"]}: {text}");
            var pdf = Encoding.Latin1.GetString(Convert.FromBase64String(text));
            pdf.Should().StartWith("%PDF-1.3");
            pdf.TrimEnd().Should().EndWith("%%EOF");
            pages.Add(PageObject().Count(pdf));
        }

        pages.Should().OnlyContain(p => p >= 1 && p <= 6);

        var after = await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        var expected = stored.Select(b =>
        {
            var copy = b.DeepClone().AsBsonDocument;
            if (copy.Contains("wasPdfDownloaded"))
            {
                copy["wasPdfDownloaded"] = true;
            }
            else
            {
                copy.Add("wasPdfDownloaded", true); // Mongoose $set of a missing path: added at the end
            }

            return copy.ToJson();
        });
        after.Select(b => b.ToJson()).Should().BeEquivalentTo(expected, "only wasPdfDownloaded is written");
        foreach (var name in seed.Collections.Keys.Where(n => n is not "budgets" and not "users")) // users: the test admin who signs in
        {
            var documents = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            documents.Select(d => d.ToJson()).Should().BeEquivalentTo(seed.Collections[name].Select(d => d.ToJson()), name + " is not touched");
        }
    }

    [GeneratedRegex(@"/Type\s*/Page(?![s\w])")]
    private static partial Regex PageObject();

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
