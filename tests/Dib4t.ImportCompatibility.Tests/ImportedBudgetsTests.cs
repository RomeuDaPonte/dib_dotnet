using System.Net;
using System.Text.Json;
using Dib4t.Client.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 3 step 1 (PLAN.md §4.0 item 3, §11.3) for budgets: after the production backup is imported, the list is
/// the legacy one (latest revision of each number among the 200 newest documents, with names), every budget opens
/// with its stored values and populated client and author, the revisions and the search work, an edit through the
/// new dialog's body writes only what legacy writes (the two flags reset, nothing else), and imported budgets of every
/// kind get a new revision and a clone with their tasks copied as stored.
/// </summary>
public sealed class ImportedBudgetsTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Imported_budgets_list_open_edit_revise_and_clone_like_legacy()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        var stored = seed.Collections["budgets"];
        var users = seed.Collections["users"].ToDictionary(u => u["_id"].AsObjectId);
        var entities = seed.Collections["entities"].ToDictionary(e => e["_id"].AsObjectId);
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        // GET /: the legacy rule, computed from the stored documents.
        var list = await client.GetFromJsonAsync<List<BudgetListItemResponse>>(new Uri("/api/budget", UriKind.Relative), Web, Cancellation);
        var expected = stored.OrderByDescending(b => b["budgetNumber"].AsString, StringComparer.Ordinal)
            .ThenByDescending(b => b.GetValue("revision", 0).ToDouble()).Take(200)
            .DistinctBy(b => b["budgetNumber"].AsString).ToList();
        list.Should().NotBeNull();
        list!.Select(r => r.Id).Should().Equal(expected.Select(b => b["_id"].ToString()));
        list!.Select(r => r.ClientName).Should().Equal(expected.Select(b => entities[b["client"].AsObjectId].GetValue("name", BsonNull.Value).AsString));
        list!.Select(r => r.MadeBy).Should().Equal(expected.Select(b => users[b["madeBy"].AsObjectId]["name"].AsString));

        // GET getById: every budget, as stored, populated.
        foreach (var document in stored)
        {
            var budget = await client.GetFromJsonAsync<BudgetResponse>(new Uri("/api/budget/getById/" + document["_id"], UriKind.Relative), Web, Cancellation);
            budget!.BudgetNumber.Should().Be(document["budgetNumber"].AsString);
            budget.Revision.Should().Be(document.GetValue("revision", 0).ToDouble());
            budget.Total.Should().Be(document.TryGetValue("total", out var total) ? total.ToDouble() : null);
            budget.Description.Should().Be(document.TryGetValue("description", out var description) ? description.AsString : null, "never trimmed or normalized");
            budget.Date.Should().Be(document["date"].ToUniversalTime());
            budget.Client!.Id.Should().Be(document["client"].ToString());
            budget.MadeBy!.Name.Should().Be(users[document["madeBy"].AsObjectId]["name"].AsString);
        }

        // Revisions and search.
        var revised = stored.Where(b => b.GetValue("revision", 0).ToDouble() > 0).Select(b => b["budgetNumber"].AsString).First();
        var revisions = await client.GetFromJsonAsync<List<BudgetRevisionResponse>>(new Uri("/api/budget/revisions/" + revised, UriKind.Relative), Web, Cancellation);
        revisions!.Select(r => r.Revision).Should().BeInDescendingOrder().And.HaveCount(stored.Count(b => b["budgetNumber"] == revised));
        var found = await client.GetFromJsonAsync<List<BudgetListItemResponse>>(new Uri("/api/budget/search?q=" + revised, UriKind.Relative), Web, Cancellation);
        found!.Select(r => r.BudgetNumber).Should().Contain(revised).And.OnlyHaveUniqueItems();

        // PUT /:id with the edit dialog's body, unchanged: legacy resets complete and wasPdfDownloaded, nothing else.
        var editable = stored.Where(b => !b.GetValue("hasGenerateJob", false).AsBoolean
                                         && b.TryGetValue("personInCharge", out var person) && person.AsString.Length > 0).ToList();
        foreach (var document in editable)
        {
            var budget = await client.GetFromJsonAsync<BudgetResponse>(new Uri("/api/budget/getById/" + document["_id"], UriKind.Relative), Web, Cancellation);
            using var saved = await client.PutAsJsonAsync(new Uri("/api/budget/" + budget!.Id, UriKind.Relative), BudgetForm.Edit(budget).ToUpdateRequest(), Web, Cancellation);
            saved.StatusCode.Should().Be(HttpStatusCode.OK, budget.BudgetNumber);
        }

        var now = (await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .ToDictionary(b => b["_id"].AsObjectId);
        var editedIds = editable.Select(b => b["_id"].AsObjectId).ToHashSet();
        foreach (var document in stored)
        {
            var legacy = document.DeepClone().AsBsonDocument;
            if (editedIds.Contains(legacy["_id"].AsObjectId))
            {
                foreach (var flag in new[] { "complete", "wasPdfDownloaded" })
                {
                    if (legacy.Contains(flag))
                    {
                        legacy[flag] = false; // a missing flag reads as its default false: not written (budget-golden.json)
                    }
                }
            }

            now[legacy["_id"].AsObjectId].ToJson().Should().Be(legacy.ToJson(), legacy["budgetNumber"].AsString + ": same BSON types and field order");
        }

        // A new revision and a clone of an imported budget: the tasks are copied as stored.
        var source = stored.OrderByDescending(b => seed.Collections["tasks"].Count(t => t["budgetId"] == b["_id"])).First();
        using var revision = await client.PostAsync(new Uri("/api/budget/newrevision/" + source["_id"], UriKind.Relative), null, Cancellation);
        revision.StatusCode.Should().Be(HttpStatusCode.OK);
        var newRevision = (await revision.Content.ReadFromJsonAsync<BudgetResponse>(Web, Cancellation))!;
        newRevision.Revision.Should().Be(stored.Count(b => b["budgetNumber"] == source["budgetNumber"]));
        var copied = await app.Database.GetCollection<BsonDocument>("tasks").Find(new BsonDocument("budgetId", ObjectId.Parse(newRevision.Id))).ToListAsync(Cancellation);
        var sourceTasks = seed.Collections["tasks"].Where(t => t["budgetId"] == source["_id"]).ToList();
        copied.Should().HaveCount(sourceTasks.Count);
        // Legacy reads each value as a JavaScript number and writes it with the js-bson rule (a Double 35.0 becomes Int32 35).
        static string Copy(BsonDocument t) => string.Join(" | ", t["taskType"].AsString, t.GetValue("description", "(missing)").ToString(),
            Dib4t.SharedKernel.JsNumber.ToString(t["unitPrice"].ToDouble()), Dib4t.SharedKernel.JsNumber.ToString(t["quantity"].ToDouble()));
        // Both apps read the source with an unsorted find (the server's natural order) and copy in that order.
        var sourceNow = await app.Database.GetCollection<BsonDocument>("tasks").Find(new BsonDocument("budgetId", source["_id"])).ToListAsync(Cancellation);
        copied.Select(Copy).Should().Equal(sourceNow.Select(Copy), "texts as stored, in the order the source is read");
        copied.Select(Copy).Should().BeEquivalentTo(sourceTasks.Select(Copy), "every task of the backup");
        static bool JsTyped(BsonValue v) => v.BsonType == (v.ToDouble() == Math.Truncate(v.ToDouble()) && v.ToDouble() is >= int.MinValue and <= int.MaxValue
            ? BsonType.Int32 : BsonType.Double);
        copied.Select(t => JsTyped(t["unitPrice"]) && JsTyped(t["quantity"])).Should().AllBeEquivalentTo(true, "the js-bson number rule (PLAN.md §4.3 rule 1)");

        var clientId = source["client"].ToString()!;
        using var clone = await client.PostAsJsonAsync(new Uri("/api/budget/clone/" + source["_id"], UriKind.Relative),
            new NewBudgetRequest("Copia de teste", "Eng Teste", TestUser.Admin.Id.ToString(), clientId), Web, Cancellation);
        var cloneText = await clone.Content.ReadAsStringAsync(Cancellation);
        clone.StatusCode.Should().Be(HttpStatusCode.OK, cloneText);
        var cloned = JsonSerializer.Deserialize<BudgetResponse>(cloneText, Web)!;
        cloned.Revision.Should().Be(0);
        stored.Select(b => b["budgetNumber"].AsString).Should().NotContain(cloned.BudgetNumber, "a new number (collision guard)");
        (await app.Database.GetCollection<BsonDocument>("tasks").CountDocumentsAsync(new BsonDocument("budgetId", ObjectId.Parse(cloned.Id)), cancellationToken: Cancellation))
            .Should().Be(sourceTasks.Count);
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
