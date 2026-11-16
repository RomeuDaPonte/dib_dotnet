using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.E2E;

/// <summary>
/// Entities (PLAN.md §7.2, §10 Phase 2): the same entity created and an existing one edited through both UIs, each
/// app on its own database loaded from the same seed backup, show the same messages and rows and leave the same
/// documents (BSON types and field order included; only the new entity's id differs); a duplicate name is refused
/// with the legacy text in both.
/// </summary>
public sealed class EntitiesTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";
    private const string NewName = "Entidade E2E Teste, Lda.";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    [Fact]
    public async Task Entity_changes_in_both_uis_show_the_same_messages_and_leave_the_same_documents()
    {
        await Harness.LoadSeedAsync(Cancellation);
        var seeded = Harness.Seed.ToSnapshot();
        // Names and conditions without surrounding spaces: the pages find them by their accessible (trimmed) names.
        static bool Plain(BsonValue value) => value.AsString == value.AsString.Trim() && !value.AsString.Contains("  ", StringComparison.Ordinal);
        var edited = seeded.Collections["entities"].Where(e => Plain(e["name"])).OrderBy(e => e["name"].AsString, StringComparer.Ordinal).First();
        var editedId = edited["_id"].AsObjectId;
        var condition = seeded.Collections["paymentmethods"].Select(m => m["name"]).First(Plain).AsString;
        var created = new EntityValues(NewName, "509 999 990", "Rua do Teste, 1", "4000-999", "Porto", "Cliente e Fornecedor", condition);
        var changes = new EntityValues(edited["name"].AsString, "500000000", "Avenida Nova, 2", "4100-001", "Matosinhos", "Cliente", condition);
        try
        {
            var results = new List<(string App, IReadOnlyList<string> Messages, IReadOnlyList<string> Row, DbSnapshot Snapshot)>();
            foreach (var app in Harness.Apps)
            {
                var session = await OpenAsync(app);
                IEntitiesPage entities = app == Harness.Legacy ? new LegacyEntitiesPage(session.Page) : new NewEntitiesPage(session.Page);
                if (app == Harness.Legacy)
                {
                    await new LegacyLoginPage(session.Page).GotoAsync();
                    await new LegacyLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }
                else
                {
                    await new NewLoginPage(session.Page).GotoAsync();
                    await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);
                }

                await entities.GotoAsync();
                var messages = new List<string>
                {
                    await AfterAsync(entities, () => entities.CreateAsync(created)),
                    await AfterAsync(entities, () => entities.EditAsync(changes.Name, changes)),
                };
                // After a reload: legacy keeps only the filtered rows after an edit until then (fixed in the new app).
                await session.Page.ReloadAsync();
                await entities.GotoAsync();
                var row = await entities.RowAsync(NewName);
                messages.Add(await AfterAsync(entities, () => entities.CreateAsync(created with { Location = "Outra" })));
                results.Add((app.Name, messages, row, await app.SnapshotAsync(Cancellation)));
            }

            var (legacy, ours) = (results[0], results[1]);
            ours.Messages.Should().Equal(legacy.Messages).And.Equal("Entidade guardada com sucesso!", "Update bem sucedido!", "Nome de entidade já registado");
            ours.Row.Take(3).Should().Equal(legacy.Row.Take(3)).And.Equal(NewName, "Porto", "Cliente e Fornecedor");
            New(ours.Snapshot).Should().Be(New(legacy.Snapshot));
            Document(ours.Snapshot, editedId).Should().Be(Document(legacy.Snapshot, editedId));
            Document(ours.Snapshot, editedId).Should().Contain("\"location\" : \"Matosinhos\"");
            Without(ours.Snapshot).DiffFrom(Without(legacy.Snapshot)).Should().BeEmpty("every other document is the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation); // back to the seed for the other tests
        }
    }

    /// <summary>Runs <paramref name="action"/> and returns the next notification's text.</summary>
    private static async Task<string> AfterAsync(IEntitiesPage page, Func<Task> action)
    {
        var before = await page.Notification.CountAsync();
        await action();
        var next = page.Notification.Nth(before);
        await next.WaitForAsync(new() { Timeout = 15_000 });
        return (await next.InnerTextAsync()).Trim();
    }

    private static string New(DbSnapshot snapshot)
    {
        var document = snapshot.Collections["entities"].Single(d => d["name"] == NewName).DeepClone().AsBsonDocument;
        document.Remove("_id");
        return document.ToJson(Canonical);
    }

    private static string Document(DbSnapshot snapshot, ObjectId id) =>
        snapshot.Collections["entities"].Single(d => d["_id"] == id).ToJson(Canonical);

    private static DbSnapshot Without(DbSnapshot snapshot) => DbSnapshot.From(snapshot.Collections.ToDictionary(
        c => c.Key,
        c => c.Value.Where(d => !(c.Key == "entities" && d["name"] == NewName))));
}
