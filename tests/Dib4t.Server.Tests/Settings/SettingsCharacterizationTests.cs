using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Dib4t.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Settings;

/// <summary>
/// The recorded legacy run (<c>characterization/01-account.json</c>, <c>02-settings.json</c>,
/// <c>07-account-delete.json</c>) replayed in order on a fresh new app (seeded like legacy at startup): every step
/// gives the same status, content type and text or JSON (key order and number text included), and leaves the same
/// documents (inserted, updated after the step, deleted), BSON types and field order included. Ids and hashes of the
/// recording are placeholders (<c>{{users:2}}</c>, <c>{{bcrypt:$2a$10$}}</c>), bound to this run's values on first sight.
/// </summary>
public sealed partial class SettingsCharacterizationTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>New-app differences, decided in PLAN.md (not replayed here; covered by their own tests).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "user-me-legacy",           // GET /api/account/me exists in the new app (§4.6)
        "user-delete-d2",           // D2: the new app deletes the requested user, not the first one
        "users-after-delete",       // follows from D2
        "budget-list-after-delete", // budgets slice (Phase 3)
    };

    private readonly Dictionary<string, string> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly HttpClient _client = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_recorded_account_and_settings_steps_replay_with_the_same_answers_and_documents()
    {
        await BindSeedAsync();
        var replayed = new List<string>();
        foreach (var file in new[] { "01-account.json", "02-settings.json", "07-account-delete.json" })
        {
            var steps = JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization(file), Cancellation)).GetProperty("steps");
            foreach (var step in steps.EnumerateArray())
            {
                var id = step.GetProperty("id").GetString()!;
                if (Skipped.Contains(id))
                {
                    continue;
                }

                await ReplayAsync(step);
                replayed.Add(id);
            }
        }

        replayed.Should().HaveCount(52, "every recorded step of the three files but the four allow-listed ones");
    }

    private async Task ReplayAsync(JsonElement step)
    {
        var id = step.GetProperty("id").GetString()!;
        var request = step.GetProperty("request");
        var body = request.GetProperty("body");
        var answer = await LegacyHttp.SendAsync(_client, new HttpMethod(request.GetProperty("method").GetString()!),
            Resolve(request.GetProperty("path").GetString()!), body.ValueKind == JsonValueKind.Null ? null : body.GetRawText(),
            await TokenAsync(request.GetProperty("auth").GetString()!), Cancellation);

        var expected = step.GetProperty("response");
        answer.Status.Should().Be(expected.GetProperty("status").GetInt32(), id);
        answer.ContentType.Should().Be(expected.GetProperty("contentType").GetString(), id);
        if (expected.TryGetProperty("jwtPayload", out var payload))
        {
            var claims = JsonElement.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(answer.Text.Split('.')[1])));
            foreach (var claim in payload.EnumerateObject().Where(c => c.Name != "iat"))
            {
                LegacyHttp.Normalize(claims.GetProperty(claim.Name)).Should().Be(LegacyHttp.Normalize(Resolve(claim.Value.GetRawText())), $"{id} {claim.Name}");
            }
        }
        else if (expected.TryGetProperty("text", out var text))
        {
            answer.Text.Should().Be(text.GetString(), id);
        }
        else
        {
            var actual = JsonElement.Parse(answer.Text);
            Bind(expected.GetProperty("json"), actual);
            LegacyHttp.Normalize(actual).Should().Be(LegacyHttp.Normalize(Resolve(expected.GetProperty("json").GetRawText())), id);
        }

        await DatabaseShouldMatchAsync(id, step.GetProperty("db"));
    }

    private async Task DatabaseShouldMatchAsync(string stepId, JsonElement db)
    {
        foreach (var collection in db.EnumerateObject())
        {
            var documents = app.Database.GetCollection<BsonDocument>(collection.Name);
            foreach (var change in collection.Value.EnumerateObject())
            {
                foreach (var item in change.Value.EnumerateArray())
                {
                    var expected = change.Name == "updated" ? item.GetProperty("after") : item;
                    var objectId = ObjectId.Parse(Resolve(item.GetProperty("_id").GetProperty("$oid").GetString()!));
                    var stored = await documents.Find(new BsonDocument("_id", objectId)).FirstOrDefaultAsync(Cancellation);
                    if (change.Name == "deleted")
                    {
                        stored.Should().BeNull($"{stepId}: {collection.Name} {objectId} deleted");
                        continue;
                    }

                    stored.Should().NotBeNull($"{stepId}: {collection.Name} {objectId}");
                    if (stored.TryGetValue("password", out var hash))
                    {
                        stored["password"] = "{{bcrypt:" + hash.AsString[..7] + "}}"; // the recording's placeholder for a hash of that format
                    }

                    stored.ToJson(Canonical).Should().Be(BsonDocument.Parse(Resolve(expected.GetRawText())).ToJson(Canonical), $"{stepId}: {collection.Name}");
                }
            }
        }
    }

    /// <summary>The recording's <c>{{users:1}}</c> (the seeded administrator) and <c>{{prices:1}}</c>.</summary>
    private async Task BindSeedAsync()
    {
        _ = app.Factory.Services; // starts the host: the seed runs before the first request
        var admin = await app.Database.GetCollection<BsonDocument>("users").Find(new BsonDocument("email", "userlogado@gmail.com")).SingleAsync(Cancellation);
        var prices = await app.Database.GetCollection<BsonDocument>("prices").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Cancellation);
        _bound["users:1"] = admin["_id"].ToString()!;
        _bound["prices:1"] = prices["_id"].ToString()!;
        _bound["oid:1"] = ObjectId.GenerateNewId().ToString();
        // "tool": the recorder's settings token (its own admin); here the seeded administrator.
        _tokens["tool"] = app.Factory.Services.GetRequiredService<ITokenService>().CreateToken(
            new User(_bound["users:1"], "User Logado", "userlogado@gmail.com", null, ["canSeeDefinicoes"], "Desenvolvimento"));
    }

    /// <summary>"admin" = Ana and "limited" = Bruno, as the recorder: logged in through the API once they exist.</summary>
    private async Task<string?> TokenAsync(string auth)
    {
        switch (auth)
        {
            case "none":
                return null;
            case "invalid":
                return "not-a-jwt";
            case "tool":
                return _tokens["tool"];
        }

        if (!_tokens.TryGetValue(auth, out var token))
        {
            var email = auth == "admin" ? "ana.admin@dib4t.local" : "bruno.limitado@dib4t.local";
            var login = await LegacyHttp.SendAsync(_client, HttpMethod.Post, "/api/account/login",
                JsonSerializer.Serialize(new { email, password = "char-test-1" }), null, Cancellation);
            login.Status.Should().Be(200, $"login of {email}");
            _tokens[auth] = token = login.Text;
        }

        return token;
    }

    /// <summary>Binds every placeholder of <paramref name="expected"/> seen for the first time to the value at the same place in <paramref name="actual"/>.</summary>
    private void Bind(JsonElement expected, JsonElement actual)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.String when Placeholder().Match(expected.GetString()!) is { Success: true } match
                                            && actual.ValueKind == JsonValueKind.String:
                _bound.TryAdd(match.Groups[1].Value, actual.GetString()!);
                break;
            case JsonValueKind.Object when actual.ValueKind == JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (actual.TryGetProperty(property.Name, out var value))
                    {
                        Bind(property.Value, value);
                    }
                }

                break;
            case JsonValueKind.Array when actual.ValueKind == JsonValueKind.Array:
                foreach (var (left, right) in expected.EnumerateArray().Zip(actual.EnumerateArray()))
                {
                    Bind(left, right);
                }

                break;
        }
    }

    private string Resolve(string text) =>
        Placeholder().Replace(text, match => _bound.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    [GeneratedRegex(@"\{\{([a-z]+:[0-9]+)\}\}")]
    private static partial Regex Placeholder();
}
