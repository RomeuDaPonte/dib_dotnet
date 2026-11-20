using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Dib4t.Server.Tests.Settings;
using Dib4t.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Dib4t.Server.Tests.Characterization;

/// <summary>
/// The recorded legacy run (<c>characterization/01-account.json</c>, <c>02-settings.json</c>, <c>03-entities.json</c>,
/// <c>04-budgets.json</c>, <c>07-account-delete.json</c>) replayed in order on a fresh new app (seeded like legacy at
/// startup): every step gives the same status, content type and text or JSON, and leaves the same documents
/// (inserted, updated after the step, deleted), BSON types and field order included. Ids and hashes of the recording
/// are placeholders (<c>{{users:2}}</c>, <c>{{bcrypt:$2a$10$}}</c>), bound to this run's values on first sight;
/// <c>{{now}}</c> is a date of this run, <c>{{yy}}</c>/<c>{{yyyy}}</c> the current year.
/// </summary>
/// <remarks>
/// JSON answers of 01–03 and 07 are compared with their key order. Budget answers (04) are compared without it:
/// legacy follows the stored order (a field added later, e.g. <c>description</c>, comes last), the new app always
/// answers in a new budget's order (a known difference, PLAN.md §10 Phase 3; the characterization README's rule).
/// </remarks>
public sealed partial class LegacyRunReplayTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>New-app differences, decided in PLAN.md (not replayed here; covered by their own tests).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "user-me-legacy",             // GET /api/account/me exists in the new app (§4.6)
        "budget-update-missing-user", // D7: the edit is validated ("madeById" is required) instead of failing on the lookup
        "budget-search-bad-regex",    // D9: 400 instead of 500
        "task-create-d8",             // D8: POST /api/task creates the task instead of refusing every body
        "user-delete-d2",             // D2: the new app deletes the requested user, not the first one
        "users-after-delete",         // follows from D2
        "budget-list-after-delete",   // follows from D2 (the deleted user is an author here)
        "job-state-d1",               // D1: the new app changes the state instead of always answering "Obra não existe"
        "job-statistics-dates-no-dates", // D17: the new app creates the state dates instead of failing (500)
        "job-register-consultancy",   // D18: the new app registers consultancy hours as development instead of ignoring them
    };

    /// <summary>
    /// D24 (owner 2026-11-16): the new app's import copies the open budget's tasks/description into the typed number
    /// instead of replacing the open budget's. The two successful legacy imports are not replayed (their own tests:
    /// <c>BudgetDetailsGoldenTests</c>); their recorded document changes are written, so the later steps (list,
    /// search) still run on the legacy state.
    /// </summary>
    /// <remarks>
    /// <c>blocked-import</c> (05): legacy refuses an import into the open budget because it has a job; with D24 the open
    /// budget only sends its tasks to the typed number, which a budget with a job may do (tested in
    /// <c>BudgetDetailsGoldenTests</c>), so the step is not sent either. The two verification steps (05) were recorded
    /// without a <c>validatedBy</c>, which legacy then leaves missing; the new app writes the signed-in user's name
    /// (PLAN.md §7.3, checked in <c>JobDetailsGoldenTests</c>), so their recorded changes are written instead.
    /// </remarks>
    private static readonly HashSet<string> LegacyStateOnly = new(StringComparer.Ordinal)
    {
        "budget-import", "budget-import-description-only", "blocked-import", "job-verifications-machining", "job-verifications-assembly",
    };

    /// <summary>
    /// The steps of <c>05-jobs.json</c> this version implements: job create (Phase 4 step 1), the budget/task routes on
    /// the budgets with a job, the jobs board's routes (step 2: statistics, due date, state, priority, the reads) and
    /// the job details page's (step 3: statistics dates, registered values, single property, verifications) and the
    /// label print (step 4: outside Production, the legacy 400); the other job steps are routes of later steps (the
    /// dashboard counters): their recorded document changes are written, so the following steps run on the legacy state.
    /// </summary>
    private static bool IsImplementedJobStep(string id) =>
        id.StartsWith("budget-", StringComparison.Ordinal) || id.StartsWith("job-create", StringComparison.Ordinal)
        || id.StartsWith("blocked-", StringComparison.Ordinal) || id.StartsWith("stats-", StringComparison.Ordinal)
        || (id.StartsWith("job-", StringComparison.Ordinal) && !LaterJobSteps.Any(p => id.StartsWith(p, StringComparison.Ordinal)));

    private static readonly string[] LaterJobSteps =
        ["job-valid-try"];

    private readonly Dictionary<string, string> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly HttpClient _client = app.Factory.CreateClient();
    private readonly DateTime _runStart = DateTime.UtcNow.AddSeconds(-1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_recorded_legacy_run_replays_with_the_same_answers_and_documents()
    {
        await BindSeedAsync();
        var replayed = new List<string>();
        var applied = new List<string>();
        foreach (var file in new[] { "01-account.json", "02-settings.json", "03-entities.json", "04-budgets.json", "05-jobs.json", "07-account-delete.json" })
        {
            var steps = JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization(file), Cancellation)).GetProperty("steps");
            foreach (var step in steps.EnumerateArray())
            {
                var id = step.GetProperty("id").GetString()!;
                if (Skipped.Contains(id))
                {
                    continue;
                }

                if (LegacyStateOnly.Contains(id) || (file == "05-jobs.json" && !IsImplementedJobStep(id)))
                {
                    await ApplyRecordedChangesAsync(step.GetProperty("db"));
                    applied.Add(id);
                    continue;
                }

                await ReplayAsync(step, ordered: file is not ("04-budgets.json" or "05-jobs.json"));
                replayed.Add(id);
            }
        }

        replayed.Should().HaveCount(192, "every recorded step of the six files but the allow-listed, the D24 ones, the verifier ones and the later job routes");
        applied.Should().HaveCount(7, "the three D24 imports, the two verifications without a verifier and the 2 steps of the dashboard counters");
    }

    private async Task ReplayAsync(JsonElement step, bool ordered)
    {
        var id = step.GetProperty("id").GetString()!;
        var request = step.GetProperty("request");
        var body = request.GetProperty("body");
        var before = await IdsAsync(step.GetProperty("db"));
        var answer = await LegacyHttp.SendAsync(_client, new HttpMethod(request.GetProperty("method").GetString()!),
            Resolve(request.GetProperty("path").GetString()!), body.ValueKind == JsonValueKind.Null ? null : Resolve(body.GetRawText()),
            await TokenAsync(request.GetProperty("auth").GetString()!), Cancellation);

        await BindInsertedAsync(step.GetProperty("db"), before);
        if (id == "login-limited" && answer.Status == 200)
        {
            // The recording's "limited" token is this login's: issued before the user is renamed, so its name stays
            // the old one (legacy writes it, e.g. in machiningStatistic.validatedBy).
            _tokens.TryAdd("limited", answer.Text);
        }

        var expected = step.GetProperty("response");
        answer.Status.Should().Be(expected.GetProperty("status").GetInt32(), $"{id}: {answer.Text}");
        answer.ContentType.Should().Be(expected.GetProperty("contentType").GetString(), id);
        if (expected.TryGetProperty("jwtPayload", out var payload))
        {
            var claims = JsonElement.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(answer.Text.Split('.')[1])));
            foreach (var claim in payload.EnumerateObject().Where(c => c.Name != "iat"))
            {
                LegacyHttp.Normalize(claims.GetProperty(claim.Name)).Should().Be(LegacyHttp.Normalize(Resolve(claim.Value.GetRawText())), $"{id} {claim.Name}");
            }
        }
        else if (expected.TryGetProperty("body", out var file))
        {
            // The PDF (legacy pdfDoc.getBase64): a base64 PDF file; its pages are compared in the PDF visual diff.
            var pdf = System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(answer.Text));
            pdf.Should().StartWith(file.GetProperty("decodedStartsWith").GetString(), id);
            pdf.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal).Should().Be(file.GetProperty("decodedEndsWithEof").GetBoolean(), id);
        }
        else if (expected.TryGetProperty("expressErrorPage", out _))
        {
            answer.Text.Should().Be("Something failed.", $"{id}: D16, the plain text instead of Express's error page");
        }
        else if (expected.TryGetProperty("text", out var text))
        {
            answer.Text.Should().Be(Resolve(text.GetString()!), id);
        }
        else
        {
            var actual = JsonElement.Parse(answer.Text);
            Bind(expected.GetProperty("json"), actual);
            Comparable(actual, ordered, isExpected: false).Should().Be(Comparable(JsonElement.Parse(CurrentName(Resolve(expected.GetProperty("json").GetRawText()))), ordered, isExpected: true), id);
        }

        await DatabaseShouldMatchAsync(id, step.GetProperty("db"));
    }

    /// <summary>The ids in the collections where the step inserts documents.</summary>
    private async Task<Dictionary<string, HashSet<ObjectId>>> IdsAsync(JsonElement db)
    {
        var ids = new Dictionary<string, HashSet<ObjectId>>(StringComparer.Ordinal);
        foreach (var collection in db.EnumerateObject().Where(c => c.Value.TryGetProperty("inserted", out _)))
        {
            var documents = await app.Database.GetCollection<BsonDocument>(collection.Name).Find(FilterDefinition<BsonDocument>.Empty)
                .Project(Builders<BsonDocument>.Projection.Include("_id")).ToListAsync(Cancellation);
            ids[collection.Name] = [.. documents.Select(d => d["_id"].AsObjectId)];
        }

        return ids;
    }

    /// <summary>
    /// Inserted documents whose id the answer does not show (e.g. the default task of a new budget): the step's new
    /// ids, in creation order, are bound to the recording's unbound placeholders in its order.
    /// </summary>
    private async Task BindInsertedAsync(JsonElement db, Dictionary<string, HashSet<ObjectId>> before)
    {
        foreach (var (name, existing) in before)
        {
            var unbound = db.GetProperty(name).GetProperty("inserted").EnumerateArray()
                .Select(d => Placeholder().Match(d.GetProperty("_id").GetProperty("$oid").GetString()!))
                .Where(m => m.Success && !_bound.ContainsKey(m.Groups[1].Value)).Select(m => m.Groups[1].Value).ToList();
            if (unbound.Count == 0)
            {
                continue;
            }

            var bound = _bound.Values.ToHashSet(StringComparer.Ordinal);
            var created = (await IdsAsync(JsonElement.Parse($"{{\"{name}\":{{\"inserted\":[]}}}}")))[name]
                .Except(existing).Where(id => !bound.Contains(id.ToString())).Order().ToList();
            foreach (var (placeholder, id) in unbound.Zip(created))
            {
                _bound[placeholder] = id.ToString();
            }
        }
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

                    MarkThisRun(stored);

                    stored.ToJson(Canonical).Should().Be(ParseRecorded(expected).ToJson(Canonical), $"{stepId}: {collection.Name}");
                }
            }
        }
    }

    /// <summary>
    /// Known difference (PLAN.md §4.6): the new app takes the signed-in user from the database, so a name the job routes
    /// write (<c>validatedBy</c>, <c>invoicedBy</c>, <c>createdBy</c>) is the user's current one; legacy writes the name
    /// inside its token, which for the limited user is the name before the rename of <c>user-update</c>.
    /// </summary>
    private static string CurrentName(string json) => StaleName().Replace(json, "$1Bruno Maquinação Silva\"");

    [GeneratedRegex("(\"(?:validatedBy|invoicedBy|createdBy)\"\\s*:\\s*\")Bruno Maquinação\"")]
    private static partial Regex StaleName();

    /// <summary>Dates of this run (also in sub-documents, e.g. <c>jobStatisticsDates</c>) as the recording's <c>{{now}}</c>.</summary>
    private void MarkThisRun(BsonDocument document)
    {
        foreach (var element in document.ToList())
        {
            if (element.Value.IsValidDateTime && IsThisRun(element.Value.ToUniversalTime()))
            {
                document[element.Name] = "{{now}}";
            }
            else if (element.Value.IsBsonDocument)
            {
                MarkThisRun(element.Value.AsBsonDocument);
            }
        }
    }

    /// <summary>A recorded <c>{{now}}</c> (also in sub-documents): the stored date when there is one, else now.</summary>
    private static void FillNow(BsonDocument document, BsonDocument? stored)
    {
        foreach (var element in document.ToList())
        {
            if (element.Value.IsString && element.Value.AsString == "{{now}}")
            {
                document[element.Name] = stored is not null && stored.TryGetValue(element.Name, out var date) && date.IsValidDateTime ? date : new BsonDateTime(DateTime.UtcNow);
            }
            else if (element.Value.IsBsonDocument)
            {
                FillNow(element.Value.AsBsonDocument, stored is not null && stored.TryGetValue(element.Name, out var sub) && sub.IsBsonDocument ? sub.AsBsonDocument : null);
            }
        }
    }

    /// <summary>A step of a later feature: its recorded inserts, updates (the whole document after it) and deletes.</summary>
    private async Task ApplyRecordedChangesAsync(JsonElement db)
    {
        foreach (var collection in db.EnumerateObject())
        {
            var documents = app.Database.GetCollection<BsonDocument>(collection.Name);
            foreach (var change in collection.Value.EnumerateObject())
            {
                foreach (var item in change.Value.EnumerateArray())
                {
                    var placeholder = item.GetProperty("_id").GetProperty("$oid").GetString()!;
                    foreach (Match match in Placeholder().Matches(item.GetRawText()))
                    {
                        // The document's id and any new sub-document id (statistics) seen here first.
                        _bound.TryAdd(match.Groups[1].Value, ObjectId.GenerateNewId().ToString());
                    }

                    var objectId = ObjectId.Parse(Resolve(placeholder));
                    var filter = new BsonDocument("_id", objectId);
                    if (change.Name == "deleted")
                    {
                        await documents.DeleteOneAsync(filter, Cancellation);
                        continue;
                    }

                    var document = ParseRecorded(change.Name == "updated" ? item.GetProperty("after") : item);
                    var stored = await documents.Find(filter).FirstOrDefaultAsync(Cancellation);
                    FillNow(document, stored);

                    await documents.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, Cancellation);
                }
            }
        }
    }

    /// <summary>A recorded document with this run's ids, years and <c>{{now}}</c> as a plain string.</summary>
    private BsonDocument ParseRecorded(JsonElement recorded) =>
        BsonDocument.Parse(RecordedNow().Replace(CurrentName(Resolve(recorded.GetRawText())), "\"{{now}}\""));

    /// <summary>
    /// The JSON text to compare: dates of this run as <c>{{now}}</c>; the recording's password hashes left out (the
    /// new app never answers them, PLAN.md §6); keys sorted when the order is not compared.
    /// </summary>
    private string Comparable(JsonElement json, bool ordered, bool isExpected)
    {
        var node = JsonNode.Parse(json.GetRawText());
        node = Rewrite(node, ordered, isExpected);
        return node?.ToJsonString() ?? "null";
    }

    private JsonNode? Rewrite(JsonNode? node, bool ordered, bool isExpected)
    {
        switch (node)
        {
            case JsonObject obj:
                var properties = obj.Select(p => (p.Key, Value: p.Value?.DeepClone()))
                    .Where(p => !(isExpected && p.Key == "password"));
                if (!ordered)
                {
                    properties = properties.OrderBy(p => p.Key, StringComparer.Ordinal);
                }

                var result = new JsonObject();
                foreach (var (key, value) in properties)
                {
                    result[key] = Rewrite(value, ordered, isExpected);
                }

                return result;
            case JsonArray array:
                return new JsonArray([.. array.Select(item => Rewrite(item?.DeepClone(), ordered, isExpected))]);
            case JsonValue value when value.TryGetValue<string>(out var text)
                                      && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
                                      && text.EndsWith('Z') && IsThisRun(date):
                return JsonValue.Create("{{now}}");
            default:
                return node;
        }
    }

    private bool IsThisRun(DateTime utc) => utc >= _runStart && utc <= DateTime.UtcNow.AddSeconds(1);

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

    private string Resolve(string text)
    {
        var year = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture);
        text = text.Replace("{{yyyy}}", year, StringComparison.Ordinal).Replace("{{yy}}", year[2..], StringComparison.Ordinal);
        return Placeholder().Replace(text, match => _bound.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    [GeneratedRegex(@"\{\{([a-z]+:[0-9]+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("""\{\s*"\$date"\s*:\s*"\{\{now\}\}"\s*\}""")]
    private static partial Regex RecordedNow();
}
