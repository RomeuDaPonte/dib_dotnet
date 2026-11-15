using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 2 (PLAN.md §4.0 item 3, §11.3) for users, prices, payment methods and printer settings: after the
/// production backup is imported, every imported document is listed and shown as stored, and saving it unchanged
/// from the settings dialogs (the bodies the UI sends) writes only what the legacy app writes for the same dialogs,
/// so the rest of the database stays byte for byte the imported one. Those writes, from the real data: the role
/// arrays of users saved by an older legacy dialog (5–6 slots) become the 7-slot form of today's dialog, with
/// <c>__v + 1</c>; the one user without a field of competencies gets the default written.
/// </summary>
public sealed class ImportedSettingsTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    /// <summary>The role checkboxes of the user dialog, in its order (legacy <c>userModal.jsx</c> <c>buildUser</c>).</summary>
    private static readonly string[] DialogRoles =
        ["canSeeOrcamentos", "canSeeObras", "canSeeAdjudicacoes", "canSeeEntidades", "canSeeDefinicoes", "canSeeDashboard", "canSeeLeituras"];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Imported_settings_are_shown_as_stored_and_an_unchanged_save_writes_nothing()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        using var client = app.CreateClient(TestUser.Admin);
        await RestoreAsync(client, await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));

        // Lists and details, as stored.
        var users = JsonElement.Parse(await GetAsync(client, "/api/account/users")).EnumerateArray().ToList();
        foreach (var stored in seed.Collections["users"])
        {
            var listed = users.Single(u => u.GetProperty("_id").GetString() == stored["_id"].ToString());
            listed.TryGetProperty("password", out _).Should().BeFalse();
            Roles(listed).Should().Equal(stored["roles"].AsBsonArray.Select(r => r.IsBsonNull ? null : r.AsString));
            listed.GetProperty("fieldOfCompetencies").GetString().Should().Be(
                stored.TryGetValue("fieldOfCompetencies", out var field) ? field.AsString : "Desenvolvimento", "the Mongoose default when missing");
            var one = JsonElement.Parse(await GetAsync(client, "/api/account/" + stored["_id"]));
            one.EnumerateObject().Select(p => p.Name).Should().Equal("_id", "name", "email", "roles");
        }

        var prices = JsonElement.Parse(await GetAsync(client, "/api/prices"));
        var storedPrices = seed.Collections["prices"].Single();
        foreach (var key in new[] { "automation", "consultancy", "development", "machining", "assembly", "margin" })
        {
            prices.GetProperty(key).GetDouble().Should().Be(storedPrices[key].ToDouble(), key);
        }

        var methods = JsonElement.Parse(await GetAsync(client, "/api/entity/getAllPaymentConditions")).EnumerateArray().ToList();
        methods.Select(m => m.GetProperty("name").GetString()).Should().Equal(seed.Collections["paymentmethods"].Select(m => m["name"].AsString),
            "natural order, names never trimmed");
        var printers = JsonElement.Parse(await GetAsync(client, "/api/printer-settings")).EnumerateArray().ToList();
        printers.Should().HaveCount(seed.Collections["printersettings"].Count);

        // Unchanged saves from the dialogs.
        var rewritten = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        foreach (var listed in users.Where(u => u.GetProperty("_id").GetString() != TestUser.Admin.Id.ToString()))
        {
            var roles = DialogRoles.Select(role => Roles(listed).Contains(role) ? role : null).ToArray();
            if (!roles.SequenceEqual(Roles(listed)))
            {
                rewritten[listed.GetProperty("_id").GetString()!] = roles;
            }

            var body = JsonSerializer.Serialize(new
            {
                name = listed.GetProperty("name").GetString(),
                email = listed.GetProperty("email").GetString(),
                password = string.Empty,
                roles,
                fieldOfCompetencies = listed.GetProperty("fieldOfCompetencies").GetString(),
            });
            (await SendAsync(client, HttpMethod.Put, "/api/account/" + listed.GetProperty("_id").GetString(), body)).Should().Be(HttpStatusCode.OK);
        }

        (await SendAsync(client, HttpMethod.Post, "/api/prices", prices.GetRawText())).Should().Be(HttpStatusCode.OK);
        foreach (var printer in printers)
        {
            var body = JsonSerializer.Serialize(new
            {
                ip = printer.GetProperty("ip").GetString(),
                port = printer.GetProperty("port").GetDouble(),
                templateName = printer.GetProperty("templateName").GetString(),
            });
            (await SendAsync(client, HttpMethod.Put, "/api/printer-settings/" + printer.GetProperty("_id").GetString(), body)).Should().Be(HttpStatusCode.OK);
        }

        var added = JsonElement.Parse(await PostTextAsync(client, "/api/entity/addPaymentMethod", """{"name":"Método temporário"}"""));
        (await SendAsync(client, HttpMethod.Delete, "/api/entity/deletePaymentMethod/" + added.GetProperty("_id").GetString(), null)).Should().Be(HttpStatusCode.OK);

        // Nothing else changed: the database is the imported one, except what the legacy dialogs write too.
        var now = await SnapshotAsync();
        var differences = now.DiffFrom(seed, maxDifferences: 500)
            .Where(d => !d.Contains(TestUser.Admin.Id.ToString(), StringComparison.Ordinal)) // the test's own signed-in user
            .ToList();
        var withoutField = seed.Collections["users"].Where(u => !u.Contains("fieldOfCompetencies")).Select(u => u["_id"].ToString()!).ToList();
        withoutField.Should().ContainSingle("the inventory's one user without fieldOfCompetencies (§4.5 item 3)");
        rewritten.Should().NotBeEmpty("production role arrays come from an older dialog");
        differences.Should().OnlyContain(d => d.StartsWith("users ", StringComparison.Ordinal)
            && (rewritten.Keys.Any(id => d.Contains(id, StringComparison.Ordinal) && (d.Contains(" roles", StringComparison.Ordinal) || d.Contains(" __v:", StringComparison.Ordinal)))
                || (d.Contains(withoutField[0], StringComparison.Ordinal) && d.Contains("fieldOfCompetencies", StringComparison.Ordinal))));
        var saved = now.Collections["users"].ToDictionary(u => u["_id"].ToString()!);
        foreach (var (id, roles) in rewritten)
        {
            var before = seed.Collections["users"].Single(u => u["_id"].ToString() == id);
            saved[id]["roles"].AsBsonArray.Select(r => r.IsBsonNull ? null : r.AsString).Should().Equal(roles);
            saved[id]["__v"].ToInt32().Should().Be(before["__v"].ToInt32() + 1, "Mongoose versioning (§4.3 rule 4)");
        }

        saved[withoutField[0]]["fieldOfCompetencies"].AsString.Should().Be("Desenvolvimento");
    }

    private static IEnumerable<string?> Roles(JsonElement user) =>
        user.GetProperty("roles").EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null : r.GetString());

    private async Task<DbSnapshot> SnapshotAsync()
    {
        var collections = new Dictionary<string, IEnumerable<BsonDocument>>();
        foreach (var name in await (await app.Database.ListCollectionNamesAsync(cancellationToken: Cancellation)).ToListAsync(Cancellation))
        {
            collections[name] = await app.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);
        }

        return DbSnapshot.From(collections);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Cancellation);
        return response.StatusCode;
    }

    private static async Task<string> PostTextAsync(HttpClient client, string path, string body)
    {
        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"), Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    private static async Task RestoreAsync(HttpClient client, byte[] backup)
    {
        using var content = new ByteArrayContent(backup);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
