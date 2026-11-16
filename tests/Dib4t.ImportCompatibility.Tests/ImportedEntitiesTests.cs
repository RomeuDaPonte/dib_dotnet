using System.Net;
using System.Text;
using System.Text.Json;
using Dib4t.Client.Entities;
using Dib4t.Contracts.Entities;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 2 (PLAN.md §4.0 item 3, §11.3) for entities: after the production backup is imported, every entity is
/// listed (sorted by name; the clients list has exactly the two client types) and answered as stored, key order
/// included; saving each one unchanged with the body the new entity dialog sends writes nothing at all; the search
/// finds imported entities.
/// </summary>
public sealed class ImportedEntitiesTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Imported_entities_are_answered_as_stored_and_an_unchanged_save_writes_nothing()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        var stored = seed.Collections["entities"];
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client);

        var all = await client.GetFromJsonAsync<List<EntityResponse>>(new Uri("/api/entity/getall", UriKind.Relative), Web, Cancellation);
        var clients = await client.GetFromJsonAsync<List<EntityResponse>>(new Uri("/api/entity/getallclients", UriKind.Relative), Web, Cancellation);

        all.Should().NotBeNull();
        all!.Select(e => e.Id).Should().BeEquivalentTo(stored.Select(d => d["_id"].ToString()));
        all.Select(e => e.Name).Should().BeInAscendingOrder(StringComparer.Ordinal, "sort(\"name\"): binary order");
        clients.Should().NotBeNull();
        clients!.Select(e => e.Id).Should().Equal(all.Where(e => e.Type is "Cliente" or "Cliente e Fornecedor").Select(e => e.Id));
        foreach (var document in stored)
        {
            using var one = await client.GetAsync(new Uri("/api/entity/" + document["_id"], UriKind.Relative), Cancellation);
            (await one.Content.ReadAsStringAsync(Cancellation)).Should().Be(AsLegacyJson(document), "answered as stored, in the stored key order");
        }

        foreach (var entity in all)
        {
            var body = EntityForm.Edit(entity).ToRequest();
            using var saved = await client.PutAsJsonAsync(new Uri("/api/entity/" + entity.Id, UriKind.Relative), body, Web, Cancellation);
            saved.StatusCode.Should().Be(HttpStatusCode.OK, entity.Name);
        }

        var first = stored[0];
        using var search = await client.PostAsJsonAsync(new Uri("/api/entity/search", UriKind.Relative),
            new EntitySearchRequest(first["name"].AsString[..3].ToUpperInvariant(), string.Empty, string.Empty, string.Empty, string.Empty), Web, Cancellation);
        (await search.Content.ReadFromJsonAsync<List<EntityResponse>>(Web, Cancellation))!.Select(e => e.Id).Should().Contain(first["_id"].ToString());

        var now = await app.Database.GetCollection<BsonDocument>("entities").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        DbSnapshot.From(new Dictionary<string, IEnumerable<BsonDocument>> { ["entities"] = now })
            .DiffFrom(DbSnapshot.From(new Dictionary<string, IEnumerable<BsonDocument>> { ["entities"] = stored }))
            .Should().BeEmpty("an unchanged save sends no update (PLAN.md §4.3 rule 3)");
    }

    /// <summary>The stored document as Express sends it: strings and <c>__v</c> in their stored order.</summary>
    private static string AsLegacyJson(BsonDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var element in document)
            {
                if (element.Value.IsObjectId || element.Value.IsString)
                {
                    writer.WriteString(element.Name, element.Value.ToString());
                }
                else
                {
                    writer.WriteNumber(element.Name, element.Value.ToInt32());
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
