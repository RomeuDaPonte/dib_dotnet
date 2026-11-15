using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Settings;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Repositories;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Settings;

/// <summary>
/// The users / prices / printers / payment-method writes on mongo:9.0.8, case by case against what the legacy models
/// write (<c>settings-golden.json</c>, from Mongoose's own <c>$__delta</c> and inserts): for every body the legacy
/// code saves, the repository's write and the legacy update applied to the same stored document give the same
/// document, BSON type and field order included; a save that changes nothing leaves the document as it was.
/// </summary>
public sealed class LegacySavesTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private const string HashPlaceholder = "$2a$10$hash-placeholder-hash-placeholder-hash-placeholder-xx";
    private static readonly JsonElement Golden = JsonElement.Parse(File.ReadAllText(Fixtures.SettingsGolden));
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task User_edits_write_what_the_legacy_save_writes()
    {
        var database = Database();
        var users = database.GetCollection<BsonDocument>(MongoCollectionNames.Users);
        var repository = new MongoUserRepository(database);
        var compared = 0;
        foreach (var item in Golden.GetProperty("editUser").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            var input = UserValidators.ValidateEdit(JsonElement.Parse(body)).Value.ToInput().Value;
            var (ours, legacy) = await TwoCopiesAsync(users, item.GetProperty("existing"));

            var found = await repository.UpdateAsync(ours.ToString(),
                new UserUpdate(input.Name, input.Email, input.FieldOfCompetencies, input.Roles, input.Password.Text is null ? null : HashPlaceholder),
                Cancellation);
            await ApplyAsync(users, legacy, item.GetProperty("update"));

            found.Should().BeTrue();
            (await WithoutIdAsync(users, ours)).Should().Be(await WithoutIdAsync(users, legacy), body);
            compared++;
        }

        compared.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public async Task Price_updates_write_what_the_legacy_save_writes()
    {
        var compared = 0;
        foreach (var item in Golden.GetProperty("prices").GetProperty("cases").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            var prices = SettingsValidators.ValidatePrices(JsonElement.Parse(body)).Value;
            var ours = Database();
            var legacy = Database();
            var existing = BsonDocument.Parse(Golden.GetProperty("prices").GetProperty("existing").GetRawText());
            await ours.GetCollection<BsonDocument>(MongoCollectionNames.Prices).InsertOneAsync(existing.DeepClone().AsBsonDocument, cancellationToken: Cancellation);
            await legacy.GetCollection<BsonDocument>(MongoCollectionNames.Prices).InsertOneAsync(existing.DeepClone().AsBsonDocument, cancellationToken: Cancellation);

            (await new MongoPricesRepository(ours).UpdateAsync(prices, Cancellation)).Should().BeTrue();
            await ApplyAsync(legacy.GetCollection<BsonDocument>(MongoCollectionNames.Prices), existing["_id"].AsObjectId, item.GetProperty("update"));

            (await OnlyDocumentAsync(ours, MongoCollectionNames.Prices)).Should().Be(await OnlyDocumentAsync(legacy, MongoCollectionNames.Prices), body);
            compared++;
        }

        compared.Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public async Task Printer_updates_write_and_answer_what_the_legacy_save_does()
    {
        var database = Database();
        var printers = database.GetCollection<BsonDocument>(MongoCollectionNames.PrinterSettings);
        var repository = new MongoPrinterSettingsRepository(database);
        var existing = Golden.GetProperty("editPrinter").GetProperty("existing");
        foreach (var item in Golden.GetProperty("editPrinter").GetProperty("cases").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            var input = SettingsValidators.ValidatePrinter(JsonElement.Parse(body)).Value;
            var (ours, legacy) = await TwoCopiesAsync(printers, existing);

            var saved = await repository.UpdateAsync(ours.ToString(), input, Cancellation);
            await ApplyAsync(printers, legacy, item.GetProperty("update"));

            (await WithoutIdAsync(printers, ours)).Should().Be(await WithoutIdAsync(printers, legacy), body);
            var response = item.GetProperty("response");
            saved.Should().Be(new Domain.Settings.PrinterSettings(ours.ToString(), response.GetProperty("ip").GetString(),
                response.GetProperty("port").GetDouble(), response.GetProperty("templateName").GetString(), response.GetProperty("__v").GetInt32()), body);
        }
    }

    [Fact]
    public async Task Inserts_store_the_legacy_documents()
    {
        var database = Database();
        var userRepository = new MongoUserRepository(database);
        foreach (var item in Golden.GetProperty("createUser").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var input = UserValidators.ValidateCreate(JsonElement.Parse(item.GetProperty("body").GetString()!)).Value.ToInput().Value;
            var id = await userRepository.InsertAsync(new NewUser(input.Name, input.Email, HashPlaceholder, input.Roles!, input.FieldOfCompetencies), Cancellation);
            (await StoredAsync(database, MongoCollectionNames.Users, id)).Should().Be(Expected(item.GetProperty("stored"), id), item.GetProperty("body").GetString());
        }

        var printerRepository = new MongoPrinterSettingsRepository(database);
        foreach (var item in Golden.GetProperty("createPrinter").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            if (!body.StartsWith("{\"ip\"", StringComparison.Ordinal))
            {
                continue; // legacy follows the body's key order; the new app always writes the UI's (ip, port, templateName)
            }

            var printer = await printerRepository.InsertAsync(SettingsValidators.ValidatePrinter(JsonElement.Parse(body)).Value, Cancellation);
            (await StoredAsync(database, MongoCollectionNames.PrinterSettings, printer.Id)).Should().Be(Expected(item.GetProperty("stored"), printer.Id), body);
        }

        var methodRepository = new MongoPaymentMethodRepository(database);
        foreach (var item in Golden.GetProperty("createPaymentMethod").EnumerateArray().Where(c => Outcome(c) == "200"))
        {
            var body = item.GetProperty("body").GetString()!;
            var method = await methodRepository.InsertAsync(SettingsValidators.ValidatePaymentMethod(JsonElement.Parse(body)).Value, Cancellation);
            (await StoredAsync(database, MongoCollectionNames.PaymentMethods, method.Id)).Should().Be(Expected(item.GetProperty("stored"), method.Id), body);
        }
    }

    [Fact]
    public async Task Users_are_listed_by_name_without_hashes_and_deleted_by_id_D2()
    {
        var database = Database();
        var users = database.GetCollection<BsonDocument>(MongoCollectionNames.Users);
        BsonDocument User(string? name, string email) => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "roles", new BsonArray { "canSeeObras", BsonNull.Value } },
            { "name", name is null ? BsonNull.Value : name }, { "email", email }, { "password", "$2a$10$secret" }, { "__v", 0 },
        };
        var first = User("Zé", "ze@dib4t.local");
        var unnamed = User(null, "sem.nome@dib4t.local");
        unnamed.Remove("name");
        var ana = User("Ana", "ana@dib4t.local");
        await users.InsertManyAsync([first, unnamed, ana], cancellationToken: Cancellation);
        var repository = new MongoUserRepository(database);

        var list = await repository.ListByNameAsync(Cancellation);
        var deleted = await repository.DeleteAsync(ana["_id"].ToString()!, Cancellation);

        list.Select(u => u.Email).Should().Equal("sem.nome@dib4t.local", "ana@dib4t.local", "ze@dib4t.local");
        list.Should().OnlyContain(u => u.PasswordHash == null, "the list never reads the hash");
        list[1].Roles.Should().Equal("canSeeObras", null);
        deleted.Should().BeTrue();
        (await users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation)).Select(d => d["_id"])
            .Should().Equal(first["_id"], unnamed["_id"]);
        (await repository.DeleteAsync(ana["_id"].ToString()!, Cancellation)).Should().BeFalse();
    }

    [Fact]
    public async Task Seeding_writes_the_legacy_initial_documents_once()
    {
        var database = Database();
        var seeder = new DefaultDataSeeder(new MongoPricesRepository(database), new MongoUserRepository(database),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultDataSeeder>.Instance);

        await seeder.SeedAsync(Cancellation);
        await seeder.SeedAsync(Cancellation);

        var initial = JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization("00-initial-db.json"), Cancellation));
        foreach (var collection in new[] { MongoCollectionNames.Prices, MongoCollectionNames.Users })
        {
            var stored = await database.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
            stored.Should().ContainSingle(collection);
            var recorded = initial.GetProperty(collection)[0].GetRawText()
                .Replace("{{bcrypt:$2b$10$}}", DefaultDataSeeder.AdminPasswordHash, StringComparison.Ordinal)
                .Replace($"{{{{{collection}:1}}}}", stored[0]["_id"].ToString(), StringComparison.Ordinal);
            stored[0].ToJson(Canonical).Should().Be(BsonDocument.Parse(recorded).ToJson(Canonical), "legacy seedBd.js shape");
        }
    }

    private IMongoDatabase Database()
    {
        MongoMappings.Register();
        return mongo.NewDatabase();
    }

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

    /// <summary>The legacy save: its $set/$unset/$inc (hash placeholder as ours), or nothing.</summary>
    private static async Task ApplyAsync(IMongoCollection<BsonDocument> collection, ObjectId id, JsonElement update)
    {
        if (update.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var json = update.GetRawText().Replace("{{bcrypt}}", HashPlaceholder, StringComparison.Ordinal);
        await collection.UpdateOneAsync(new BsonDocument("_id", id), BsonDocument.Parse(json), cancellationToken: Cancellation);
    }

    private static async Task<string> WithoutIdAsync(IMongoCollection<BsonDocument> collection, ObjectId id)
    {
        var document = await collection.Find(new BsonDocument("_id", id)).SingleAsync(Cancellation);
        document.Remove("_id");
        return document.ToJson(Canonical);
    }

    private static async Task<string> OnlyDocumentAsync(IMongoDatabase database, string collection)
    {
        var document = await database.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Cancellation);
        document.Remove("_id");
        return document.ToJson(Canonical);
    }

    private static async Task<string> StoredAsync(IMongoDatabase database, string collection, string id) =>
        (await database.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(Cancellation))
        .ToJson(Canonical);

    private static string Expected(JsonElement stored, string id)
    {
        var json = stored.GetRawText()
            .Replace("\"{{oid}}\"", $$"""{"$oid":"{{id}}"}""", StringComparison.Ordinal)
            .Replace("{{bcrypt}}", HashPlaceholder, StringComparison.Ordinal);
        return BsonDocument.Parse(json).ToJson(Canonical);
    }
}
