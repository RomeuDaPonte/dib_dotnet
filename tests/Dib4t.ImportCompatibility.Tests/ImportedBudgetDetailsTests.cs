using System.Net;
using System.Text.Json;
using Dib4t.Client.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.SharedKernel;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 3 step 2 (PLAN.md §4.0 item 3, §11.3) for the budget details: after the production backup is imported, every
/// budget's tasks are listed as stored (natural order, values, missing descriptions), and an imported budget used
/// in the details page without changing anything (every header input and every task line focused and left, with
/// the request bodies the page sends) writes only what legacy writes: the two flags reset, no task and no other
/// field changed (BSON types, field order and the 6 non-NFC texts of production kept). Budgets with a job stay
/// untouched (the patch answers them unchanged, the task routes refuse). An import (D24) adds copies of the tasks of
/// the budget with most tasks (as stored, js-bson number rule) and its description to the highest revision of another
/// number, recomputes that budget's total and resets its flags, and deletes or changes nothing else.
/// </summary>
public sealed class ImportedBudgetDetailsTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Imported_budgets_open_and_save_unchanged_in_the_details_page_like_legacy()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        var stored = seed.Collections["budgets"];
        var storedTasks = seed.Collections["tasks"];
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        // GET getBudgetTasks: every budget's tasks, as stored, in the server's natural order (an unsorted find, as legacy;
        // the same order the legacy database lists them in, checked on mongo:4.1.4; the seed snapshot is sorted by id).
        var naturalOrder = await app.Database.GetCollection<BsonDocument>("tasks").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        naturalOrder.Select(t => t.ToJson()).Should().BeEquivalentTo(storedTasks.Select(t => t.ToJson()), "the restore keeps every task as it is");
        var tasksByBudget = naturalOrder.GroupBy(t => t["budgetId"].AsObjectId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new Dictionary<ObjectId, List<BudgetTaskRow>>();
        foreach (var document in stored)
        {
            var id = document["_id"].AsObjectId;
            var list = await client.GetFromJsonAsync<List<BudgetTaskResponse>>(new Uri("/api/task/getBudgetTasks/" + id, UriKind.Relative), Web, Cancellation);
            var expected = tasksByBudget.GetValueOrDefault(id) ?? [];
            list!.Select(t => t.Id).Should().Equal(expected.Select(t => t["_id"].ToString()), document["budgetNumber"].AsString);
            list!.Select(t => (t.TaskType, t.Description, t.UnitPrice, t.Quantity)).Should().Equal(expected.Select(t => (
                t.GetValue("taskType", BsonNull.Value).IsString ? t["taskType"].AsString : null,
                t.TryGetValue("description", out var description) ? description.AsString : null,
                t.TryGetValue("unitPrice", out var unitPrice) ? unitPrice.ToDouble() : (double?)null,
                t.TryGetValue("quantity", out var quantity) ? quantity.ToDouble() : (double?)null)), "never trimmed or normalized");
            rows[id] = [.. list!.Select(t => new BudgetTaskRow(t))];
        }

        // The details page, used without changing anything: every header input and every task line focused and left.
        var editable = stored.Where(b => !b.GetValue("hasGenerateJob", false).AsBoolean).ToList();
        var withJob = stored.Where(b => b.GetValue("hasGenerateJob", false).AsBoolean).Take(20).ToList();
        foreach (var document in editable.Concat(withJob))
        {
            var budget = await client.GetFromJsonAsync<BudgetResponse>(new Uri("/api/budget/getById/" + document["_id"], UriKind.Relative), Web, Cancellation);
            var hasJob = budget!.HasGenerateJob;
            var patches = new (string Field, object? Value)[]
            {
                (BudgetFields.Margin, JsInput.Of(budget.Margin).Json),
                (BudgetFields.Total, JsInput.Of(budget.Total).Json),
                (BudgetFields.DaysNeededToDoTheWork, JsInput.Of(budget.DaysNeededToDoTheWork).Json),
                (BudgetFields.Validity, JsInput.Of(budget.Validity).Json),
                (BudgetFields.PaymentMethod, budget.PaymentMethod),
                (BudgetFields.Description, budget.Description),
            };
            foreach (var (field, value) in patches)
            {
                using var patched = await client.PutAsJsonAsync(new Uri("/api/budget/patchProperty/" + budget.Id, UriKind.Relative),
                    new PatchBudgetRequest(field, value), Web, Cancellation);
                var text = await patched.Content.ReadAsStringAsync(Cancellation);
                if (value is null or "")
                {
                    // Legacy too: a missing total or description (old documents) is refused by Joi.
                    (patched.StatusCode, text).Should().Be((HttpStatusCode.BadRequest, "Não é possível atualizar " + field), budget.BudgetNumber);
                }
                else
                {
                    patched.StatusCode.Should().Be(HttpStatusCode.OK, $"{budget.BudgetNumber} {field}: {text}");
                }
            }

            foreach (var row in rows[document["_id"].AsObjectId])
            {
                using var saved = await client.PutAsJsonAsync(new Uri($"/api/task/{row.Id}/{row.BudgetId}", UriKind.Relative), row.ToUpdateRequest(), Web, Cancellation);
                var text = await saved.Content.ReadAsStringAsync(Cancellation);
                if (hasJob)
                {
                    (saved.StatusCode, text).Should().Be((HttpStatusCode.BadRequest, "Orçamento gerou obra!"));
                }
                else
                {
                    saved.StatusCode.Should().Be(HttpStatusCode.OK, $"{budget.BudgetNumber} task {row.Id}: {text}");
                }
            }
        }

        var tasksNow = await app.Database.GetCollection<BsonDocument>("tasks").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        tasksNow.Select(t => t.ToJson()).Should().Equal(naturalOrder.Select(t => t.ToJson()), "an unchanged task save writes nothing (types, order, texts kept)");
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
                        legacy[flag] = false;
                    }
                }
            }

            now[legacy["_id"].AsObjectId].ToJson().Should().Be(legacy.ToJson(), legacy["budgetNumber"].AsString + ": only the flags reset");
        }

        // Import (D24): the budget with most tasks sends its tasks and description into the highest revision of another
        // number; tasks are added (copies as stored, js-bson number rule), nothing is deleted, the open budget is unchanged.
        var open = stored.OrderByDescending(b => tasksByBudget.GetValueOrDefault(b["_id"].AsObjectId)?.Count ?? 0).First();
        var latest = stored.GroupBy(b => b["budgetNumber"].AsString)
            .Select(g => g.OrderByDescending(b => b.GetValue("revision", 0).ToDouble()).First()).ToList();
        var target = latest.First(b => b["budgetNumber"] != open["budgetNumber"] && !b.GetValue("hasGenerateJob", false).AsBoolean
                                       && tasksByBudget.ContainsKey(b["_id"].AsObjectId));
        var budgetsBefore = (await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .ToDictionary(b => b["_id"].AsObjectId);
        var tasksBefore = await app.Database.GetCollection<BsonDocument>("tasks").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        using var imported = await client.PutAsJsonAsync(new Uri("/api/budget/import/" + open["_id"], UriKind.Relative),
            new ImportBudgetRequest(true, true, target["budgetNumber"].AsString), Web, Cancellation);
        var importedText = await imported.Content.ReadAsStringAsync(Cancellation);
        imported.StatusCode.Should().Be(HttpStatusCode.OK, importedText);
        JsonSerializer.Deserialize<BudgetResponse>(importedText, Web)!.Id.Should().Be(target["_id"].ToString(), "the receiving budget");

        var tasksAfter = await app.Database.GetCollection<BsonDocument>("tasks").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        var openTasks = tasksByBudget[open["_id"].AsObjectId];
        tasksAfter.Take(tasksBefore.Count).Select(t => t.ToJson()).Should().Equal(tasksBefore.Select(t => t.ToJson()), "nothing deleted or changed");
        var added = tasksAfter.Skip(tasksBefore.Count).ToList();
        static string Copy(BsonDocument t) => string.Join(" | ", t["taskType"].AsString, t.GetValue("description", "(missing)").ToString(),
            JsNumber.ToString(t["unitPrice"].ToDouble()), JsNumber.ToString(t["quantity"].ToDouble()));
        added.Select(Copy).Should().Equal(openTasks.Select(Copy), "copies of the open budget's tasks, in natural order");
        added.Should().AllSatisfy(t => t["budgetId"].Should().Be(target["_id"]));
        added.Select(t => t.Names.ToArray()[1]).Should().AllBe("taskType", "the copies' field order (taskType before budgetId)");

        var budgetsAfter = (await app.Database.GetCollection<BsonDocument>("budgets").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation))
            .ToDictionary(b => b["_id"].AsObjectId);
        var receiving = budgetsAfter[target["_id"].AsObjectId];
        var all = tasksAfter.Where(t => t["budgetId"] == target["_id"]).Select(t => (t["quantity"].ToDouble(), t["unitPrice"].ToDouble()));
        var tasksTotal = Dib4t.SharedKernel.Calculations.BudgetCalculations.TasksTotal(all);
        receiving["total"].ToDouble().Should().Be(Dib4t.SharedKernel.Calculations.BudgetCalculations.TotalFromTasks(tasksTotal, receiving.GetValue("margin", 30).ToDouble()));
        receiving.GetValue("complete", false).AsBoolean.Should().BeFalse();
        receiving.GetValue("wasPdfDownloaded", false).AsBoolean.Should().BeFalse();
        receiving.GetValue("description", BsonNull.Value).Should().Be(open.TryGetValue("description", out var openDescription)
            ? openDescription : budgetsBefore[target["_id"].AsObjectId].GetValue("description", BsonNull.Value));
        foreach (var (id, before) in budgetsBefore.Where(b => b.Key != target["_id"].AsObjectId))
        {
            budgetsAfter[id].ToJson().Should().Be(before.ToJson(), "only the receiving budget changes");
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
