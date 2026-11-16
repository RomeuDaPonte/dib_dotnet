using System.Text.Json;
using Dib4t.Application.Entities;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Repositories;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Entities;

/// <summary>
/// The entity writes and queries on mongo:9.0.8 against what the legacy code does (<c>entity-golden.json</c>, from
/// Mongoose's own inserts and <c>$__delta</c>): for every body the legacy code saves, the repository's write and the
/// legacy update applied to the same stored document give the same document, BSON type and field order included;
/// the search patterns behave on MongoDB as the legacy ones; lists sort like <c>sort("name")</c>.
/// </summary>
public sealed class EntityRepositoryTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    /// <summary>The legacy UI's field order (entityService.js); the new app always writes this one (ADR-0004).</summary>
    private static readonly string[] UiOrder = ["name", "fiscalNumber", "address", "zipCode", "location", "paymentCondition", "type"];

    /// <summary>Patterns JavaScript accepts but MongoDB's PCRE refuses: the legacy query fails there too (500 in both apps).</summary>
    private static readonly string[] RefusedByMongo = ["^\\k"];

    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.EntityGolden));
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Edits_write_what_the_legacy_save_writes_and_answer_the_saved_entity()
    {
        var database = Database();
        var entities = database.GetCollection<BsonDocument>(MongoCollectionNames.Entities);
        var repository = new MongoEntityRepository(database);
        var compared = 0;
        foreach (var item in Golden.GetProperty("editEntity").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var existing = item.GetProperty("existing");
            if (existing.TryGetProperty("fiscalNumber", out var stored) && stored.ValueKind == JsonValueKind.Object)
            {
                continue; // a stored number: Mongoose casts every write to a string, so neither app ever stores one (§4.5)
            }

            var body = item.GetProperty("body").GetString()!;
            var input = EntityValidators.Validate(JsonElement.Parse(body)).Value.ToInput().Value;
            var (ours, legacy) = await TwoCopiesAsync(entities, existing);

            var saved = await repository.UpdateAsync(ours.ToString(), input, Cancellation);
            await ApplyAsync(entities, legacy, item.GetProperty("update"));

            (await WithoutIdAsync(entities, ours)).Should().Be(await WithoutIdAsync(entities, legacy), body);
            var response = item.GetProperty("response");
            saved.Should().NotBeNull();
            saved!.Name.Should().Be(Text(response, "name"), body);
            (saved.FiscalNumber, saved.Address, saved.ZipCode, saved.Location, saved.PaymentCondition, saved.Type).Should().Be(
                (Text(response, "fiscalNumber"), Text(response, "address"), Text(response, "zipCode"), Text(response, "location"),
                    Text(response, "paymentCondition"), Text(response, "type")), body);
            compared++;
        }

        compared.Should().BeGreaterThanOrEqualTo(20);
        (await repository.UpdateAsync(ObjectId.GenerateNewId().ToString(), Input(), Cancellation)).Should().BeNull();
    }

    [Fact]
    public async Task Inserts_store_the_legacy_documents()
    {
        var database = Database();
        var repository = new MongoEntityRepository(database);
        var compared = 0;
        foreach (var item in Golden.GetProperty("createEntity").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            if (!InUiOrder(body))
            {
                continue; // legacy follows the body's key order; the new app always writes the UI's
            }

            var input = EntityValidators.Validate(JsonElement.Parse(body)).Value.ToInput().Value;
            var entity = await repository.InsertAsync(input, Cancellation);

            var stored = await database.GetCollection<BsonDocument>(MongoCollectionNames.Entities)
                .Find(new BsonDocument("_id", ObjectId.Parse(entity.Id))).SingleAsync(Cancellation);
            var expected = item.GetProperty("stored").GetRawText().Replace("\"{{oid}}\"", $$"""{"$oid":"{{entity.Id}}"}""", StringComparison.Ordinal);
            stored.ToJson(Canonical).Should().Be(Nfc(BsonDocument.Parse(expected)).ToJson(Canonical), body);
            entity.Version.Should().Be(0);
            compared++;
        }

        compared.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public async Task Search_patterns_match_and_fail_on_mongodb_like_the_legacy_ones()
    {
        var database = Database();
        var entities = database.GetCollection<BsonDocument>(MongoCollectionNames.Entities);
        await entities.InsertManyAsync(
        [
            Document("Metalúrgica do Ave", "500100200", "Rua da Indústria", "4760-100", "Porto"),
            Document("metalomecânica Sul", "500300400", "Rua Nova", "4000-001", "Portimão"),
            Document("Aços Porto", "509000000", "Av. Central", "4000-002", "Lisboa"),
            Document("undefined Lda.", "undefined", "undefined", "undefined", "undefined"),
        ], cancellationToken: Cancellation);
        var repository = new MongoEntityRepository(database);

        async Task<string[]?> SearchAsync(string body) =>
            (await repository.SearchAsync(EntitySearch.From(JsonElement.Parse(body)).Value, Cancellation))?.Select(e => e.Name!).ToArray();

        (await SearchAsync("""{"name":"met","nif":"","localidade":"Port","morada":"Rua","codigoPostal":""}"""))
            .Should().Equal(["Metalúrgica do Ave", "metalomecânica Sul"], "prefix, any case, every field (AND), natural order");
        (await SearchAsync("""{"name":"","nif":"","localidade":"","morada":"","codigoPostal":"4000"}""")).Should().Equal(["metalomecânica Sul", "Aços Porto"]);
        (await SearchAsync("""{"name":"Aços|Met","nif":"","localidade":"","morada":"","codigoPostal":""}""")).Should().HaveCount(3, "the text is a regular expression");
        (await SearchAsync("""{"name":"Met"}""")).Should().BeEmpty("a missing key is the text \"undefined\"");
        (await SearchAsync("{}")).Should().Equal(["undefined Lda."]);
        (await SearchAsync("""{"name":"(","nif":"","localidade":"","morada":"","codigoPostal":""}""")).Should().BeNull("an invalid pattern fails");

        foreach (var item in Golden.GetProperty("search").EnumerateArray())
        {
            var body = item.GetProperty("body").GetString()!;
            var search = EntitySearch.From(JsonElement.Parse(body));
            if (!search.IsSuccess)
            {
                continue; // refused before the query (Dib4t.Application.Tests)
            }

            var legacyFails = Outcome(item) == "500" || search.Value.Patterns.Any(p => RefusedByMongo.Contains(p.Pattern));
            var found = await repository.SearchAsync(search.Value, Cancellation);
            (found is null).Should().Be(legacyFails, body);
        }
    }

    [Fact]
    public async Task Lists_sort_by_name_clients_are_the_two_client_types_and_names_match_exactly()
    {
        var database = Database();
        var entities = database.GetCollection<BsonDocument>(MongoCollectionNames.Entities);
        var unnamed = Document("x", "", "", "", "");
        unnamed.Remove("name");
        await entities.InsertManyAsync(
        [
            Document("Zeta", type: "Cliente"),
            Document("alfa", type: "Fornecedor"),
            Document("Ômega", type: "Cliente e Fornecedor"),
            Document("Beta", type: "Cliente"),
            unnamed,
        ], cancellationToken: Cancellation);
        var repository = new MongoEntityRepository(database);

        var all = await repository.ListByNameAsync(Cancellation);
        var clients = await repository.ListClientsAsync(Cancellation);

        all.Select(e => e.Name).Should().Equal([null, "Beta", "Zeta", "alfa", "Ômega"]);
        clients.Select(e => e.Name).Should().Equal([null, "Beta", "Zeta", "Ômega"]);
        (await repository.ExistsByNameAsync("Beta", Cancellation)).Should().BeTrue();
        (await repository.ExistsByNameAsync("beta", Cancellation)).Should().BeFalse("exact and case-sensitive, like the legacy findOne by name");
        var beta = all[1];
        (await repository.FindByIdAsync(beta.Id, Cancellation)).Should().Be(beta);
        (await repository.FindByIdAsync(ObjectId.GenerateNewId().ToString(), Cancellation)).Should().BeNull();
    }

    private IMongoDatabase Database()
    {
        MongoMappings.Register();
        return mongo.NewDatabase();
    }

    private static EntityInput Input() =>
        new("Nova", EntityText.Missing, EntityText.Missing, EntityText.Missing, EntityText.Missing, "30 dias", "Cliente");

    private static BsonDocument Document(string name, string fiscalNumber = "", string address = "", string zipCode = "", string location = "", string type = "Cliente") => new()
    {
        { "_id", ObjectId.GenerateNewId() }, { "name", name }, { "fiscalNumber", fiscalNumber }, { "address", address }, { "zipCode", zipCode },
        { "location", location }, { "paymentCondition", "30 dias" }, { "type", type }, { "__v", 0 },
    };

    /// <summary>Whether the body's keys come in the UI's order (missing keys allowed).</summary>
    private static bool InUiOrder(string body)
    {
        var keys = JsonElement.Parse(body).EnumerateObject().Select(p => Array.IndexOf(UiOrder, p.Name)).ToList();
        return keys.All(k => k >= 0) && keys.SequenceEqual(keys.Order()) && keys.Distinct().Count() == keys.Count;
    }

    /// <summary>The new app stores NFC (§4.3 rule 2); legacy stores the text as sent.</summary>
    private static BsonDocument Nfc(BsonDocument document)
    {
        foreach (var element in document.ToList())
        {
            if (element.Value.IsString)
            {
                document[element.Name] = element.Value.AsString.Normalize(System.Text.NormalizationForm.FormC);
            }
        }

        return document;
    }

    private static string? Text(JsonElement response, string field) =>
        response.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Normalize(System.Text.NormalizationForm.FormC)
            : null;

    private static string? Outcome(JsonElement item) => item.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : null;

    private static async Task<(ObjectId Ours, ObjectId Legacy)> TwoCopiesAsync(IMongoCollection<BsonDocument> collection, JsonElement existing)
    {
        var ours = BsonDocument.Parse(existing.GetRawText());
        var legacy = ours.DeepClone().AsBsonDocument;
        ours["_id"] = ObjectId.GenerateNewId();
        legacy["_id"] = ObjectId.GenerateNewId();
        await collection.InsertManyAsync([ours, legacy], cancellationToken: Cancellation);
        return (ours["_id"].AsObjectId, legacy["_id"].AsObjectId);
    }

    /// <summary>The legacy save (NFC like ours: the golden bodies' decomposed names are stored composed by the new app).</summary>
    private static async Task ApplyAsync(IMongoCollection<BsonDocument> collection, ObjectId id, JsonElement update)
    {
        if (update.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var document = BsonDocument.Parse(update.GetRawText());
        if (document.TryGetValue("$set", out var set))
        {
            Nfc(set.AsBsonDocument);
        }

        await collection.UpdateOneAsync(new BsonDocument("_id", id), document, cancellationToken: Cancellation);
    }

    private static async Task<string> WithoutIdAsync(IMongoCollection<BsonDocument> collection, ObjectId id)
    {
        var document = await collection.Find(new BsonDocument("_id", id)).SingleAsync(Cancellation);
        document.Remove("_id");
        return document.ToJson(Canonical);
    }
}
