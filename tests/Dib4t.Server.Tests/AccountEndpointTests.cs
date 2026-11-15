using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dib4t.Tests.Common;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Server.Tests;

/// <summary>
/// <c>POST /api/account/login</c> and <c>GET /api/account/me</c>, and the token check of every protected route
/// (PLAN.md §4.6, §6), against the recorded legacy steps (<c>characterization/01-account.json</c>), the legacy
/// code's own validation answers (auth golden file) and a real mongo:9.0.8.
/// </summary>
public sealed class AccountEndpointTests(AccountEndpointTests.App app) : IClassFixture<AccountEndpointTests.App>, IAsyncLifetime
{
    private static readonly Uri LoginUri = new("/api/account/login", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/account/me", UriKind.Relative);

    private readonly HttpClient _client = app.Factory.CreateClient();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await TestUser.Admin.SaveAsync(app.Database, Cancellation);
        await TestUser.Limited.SaveAsync(app.Database, Cancellation);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TheoryData<string> RecordedLoginSteps =>
        ["login-validation", "login-unknown-email", "login-wrong-password", "login-admin", "login-limited"];

    [Theory]
    [MemberData(nameof(RecordedLoginSteps))]
    public async Task Login_matches_the_recorded_legacy_step(string stepId)
    {
        var step = await RecordedStepAsync("01-account.json", stepId);
        var expected = step.GetProperty("response");

        using var response = await PostJsonAsync(LoginUri, step.GetProperty("request").GetProperty("body").GetRawText());
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        if (expected.TryGetProperty("jwtPayload", out var recordedPayload))
        {
            var user = stepId == "login-admin" ? TestUser.Admin : TestUser.Limited;
            var payload = Payload(text);
            payload.EnumerateObject().Select(p => p.Name).Should().Equal(recordedPayload.EnumerateObject().Select(p => p.Name),
                "same claims in the same order as the legacy token");
            payload.GetProperty("_id").GetString().Should().Be(user.Id.ToString());
            foreach (var claim in new[] { "name", "email", "roles", "fieldOfCompetencies" })
            {
                JsonElement.DeepEquals(payload.GetProperty(claim), recordedPayload.GetProperty(claim)).Should().BeTrue(claim);
            }

            payload.GetProperty("iat").GetInt64().Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 60);
            payload.TryGetProperty("exp", out _).Should().BeFalse("tokens never expire (§4.6)");
        }
        else
        {
            text.Should().Be(expected.GetProperty("text").GetString());
        }
    }

    public static TheoryData<string, string?> GoldenBodies
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var item in JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)).GetProperty("loginValidation").EnumerateArray())
            {
                data.Add(item.GetProperty("body").GetString()!, item.GetProperty("message").GetString());
            }

            return data;
        }
    }

    /// <summary>Every body the legacy code was asked about: same 400 text, or past validation (then 400 e-mail/password texts or 200).</summary>
    [Theory]
    [MemberData(nameof(GoldenBodies))]
    public async Task Login_validation_answers_like_the_legacy_code(string body, string? message)
    {
        using var response = await PostJsonAsync(LoginUri, body);
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        if (message is not null)
        {
            ((int)response.StatusCode, text).Should().Be((400, message));
        }
        else
        {
            text.Should().NotStartWith("\"", "the body passed the legacy validation");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task A_body_that_is_not_JSON_counts_as_empty_like_express_json(string? contentType)
    {
        using var content = new StringContent("""{"email":"ana.admin@dib4t.local","password":"char-test-1"}""", Encoding.UTF8);
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);

        using var response = await _client.PostAsync(LoginUri, content, Cancellation);

        ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation)).Should().Be((400, "\"email\" is required"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"email\":")]
    [InlineData("\"ana.admin@dib4t.local\"")]
    [InlineData("null")]
    public async Task Invalid_JSON_is_a_plain_400(string body)
    {
        using var response = await PostJsonAsync(LoginUri, body);
        var text = await response.Content.ReadAsStringAsync(Cancellation);

        if (body.Length == 0)
        {
            ((int)response.StatusCode, text).Should().Be((400, "\"email\" is required"), "express.json reads an empty body as an empty object");
        }
        else
        {
            ((int)response.StatusCode, text).Should().Be((400, "Bad Request"));
        }
    }

    [Fact]
    public async Task A_body_over_100_kB_is_413()
    {
        using var response = await PostJsonAsync(LoginUri, $$"""{"email":"{{new string('a', 102_400)}}"}""");

        ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation)).Should().Be((413, "request entity too large"));
    }

    [Fact]
    public async Task The_login_token_opens_me_with_the_user_as_stored()
    {
        var token = await LoginAsync(TestUser.Admin);
        await app.Database.GetCollection<BsonDocument>("users").UpdateOneAsync(
            new BsonDocument("_id", TestUser.Admin.Id), new BsonDocument("$push", new BsonDocument("roles", BsonNull.Value)), cancellationToken: Cancellation);

        using var response = await GetAsync(MeUri, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be(
            $$"""{"_id":"{{TestUser.Admin.Id}}","name":"Ana Administradora","email":"ana.admin@dib4t.local","roles":["canSeeOrcamentos","canSeeObras","canSeeHistorico","canSeeEntidades","canSeeDefinicoes","canSeeDashboard","canSeeLeituras",null],"fieldOfCompetencies":"Desenvolvimento"}""");
    }

    /// <summary>Recorded on <c>GET /api/prices</c> (not ported yet); the same middleware answers on every T route.</summary>
    [Theory]
    [InlineData("auth-no-token", null)]
    [InlineData("auth-no-token", "")]
    [InlineData("auth-invalid-token", "not-a-jwt")]
    public async Task Missing_or_invalid_token_matches_the_recorded_legacy_step(string stepId, string? token)
    {
        var expected = (await RecordedStepAsync("01-account.json", stepId)).GetProperty("response");

        using var response = await GetAsync(MeUri, token);

        ((int)response.StatusCode).Should().Be(expected.GetProperty("status").GetInt32());
        response.Content.Headers.ContentType!.ToString().Should().Be(expected.GetProperty("contentType").GetString());
        (await response.Content.ReadAsStringAsync(Cancellation)).Should().Be(expected.GetProperty("text").GetString());
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Fact]
    public async Task Tokens_of_other_keys_with_a_Bearer_prefix_or_unsigned_are_invalid()
    {
        var valid = await LoginAsync(TestUser.Admin);
        var parts = valid.Split('.');
        string[] tokens =
        [
            "Bearer " + valid,
            parts[0] + "." + parts[1] + ".",                                    // no signature
            "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0." + parts[1] + ".",          // alg: none
            parts[0] + "." + parts[1] + "." + new string('A', parts[2].Length), // wrong signature
            // The same claims signed with the legacy app's hard-coded key: the systems are separate (§4.6).
            LegacyToken(parts[1], "naoSeguro"),
        ];

        foreach (var token in tokens)
        {
            using var response = await GetAsync(MeUri, token);
            ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation)).Should().Be((400, "Invalid token."), token);
        }
    }

    [Fact]
    public async Task Anonymous_routes_and_pages_ignore_the_token_header()
    {
        // A token of a user that does not exist would be 400 wherever it is checked.
        var ghost = (TestUser.Admin with { Id = ObjectId.GenerateNewId() }).TokenFrom(app.Factory.Services);
        using var login = new HttpRequestMessage(HttpMethod.Post, LoginUri)
        {
            Content = new StringContent("""{"email":"ana.admin@dib4t.local","password":"char-test-1"}""", Encoding.UTF8, "application/json"),
        };
        login.Headers.Add("x-auth-token", ghost);

        using var loginResponse = await _client.SendAsync(login, Cancellation);
        using var page = await GetAsync(new Uri("/", UriKind.Relative), ghost);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_valid_token_of_a_user_that_does_not_exist_is_invalid()
    {
        var ghost = TestUser.Admin with { Id = ObjectId.GenerateNewId(), Email = "ghost@dib4t.local" };

        using var response = await GetAsync(MeUri, ghost.TokenFrom(app.Factory.Services));

        ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation)).Should().Be((400, "Invalid token."));
    }

    /// <summary>PLAN.md §4.6: roles come from the database (cached briefly), so a change needs no new login.</summary>
    [Fact]
    public async Task A_removed_role_and_a_deleted_user_take_effect_after_the_cache_window()
    {
        var carla = new TestUser(ObjectId.GenerateNewId(), "Carla Definições", "carla.definicoes@dib4t.local", ["canSeeDefinicoes"]);
        await carla.SaveAsync(app.Database, Cancellation);
        var token = await LoginAsync(carla);
        var users = app.Database.GetCollection<BsonDocument>("users");

        (await RestoreStatusAsync(token)).Should().Be((400, "Nenhum ficheiro de backup recebido."));
        await users.UpdateOneAsync(new BsonDocument("_id", carla.Id), new BsonDocument("$set", new BsonDocument("roles", new BsonArray())), cancellationToken: Cancellation);
        (await RestoreStatusAsync(token)).Should().Be((400, "Nenhum ficheiro de backup recebido."), "still cached");
        await Task.Delay(App.UserCacheDuration + TimeSpan.FromMilliseconds(100), Cancellation);
        (await RestoreStatusAsync(token)).Should().Be((401, "Não tem permissão para concluir esta acção."));

        await users.DeleteOneAsync(new BsonDocument("_id", carla.Id), Cancellation);
        await Task.Delay(App.UserCacheDuration + TimeSpan.FromMilliseconds(100), Cancellation);
        (await RestoreStatusAsync(token)).Should().Be((400, "Invalid token."));
    }

    [Fact]
    public async Task Logins_are_logged_in_Auth_with_the_email_and_without_secrets()
    {
        await PostJsonAsync(LoginUri, """{"email":"bruno.limitado@dib4t.local","password":"wrong-pass-123"}""");
        var token = await LoginAsync(TestUser.Limited);
        await GetAsync(MeUri, token);
        await GetAsync(MeUri, "not-a-jwt");

        var rows = await LogStoreFile.WaitForAsync(app.LogStorePath, r =>
            r.Count(x => x.Category == "Auth") >= 3 && r.Any(x => x.Category == "Http" && x.UserName == "Bruno Maquinação"), cancellationToken: Cancellation);

        rows.Where(r => r.Category == "Auth").Select(r => r.Message).Should().Contain(
        [
            "Login failed for bruno.limitado@dib4t.local (user 5f1e9a2b3c4d5e6f70819204): wrong password",
            "Login succeeded for bruno.limitado@dib4t.local (user 5f1e9a2b3c4d5e6f70819204)",
            "Invalid token on GET /api/account/me",
        ]);
        rows.Should().Contain(r => r.Category == "Http" && r.UserName == "Bruno Maquinação" && r.Message.StartsWith("HTTP GET /api/account/me responded 200", StringComparison.Ordinal),
            "the request entry carries the signed-in user (§3.3)");
        foreach (var row in rows)
        {
            var all = string.Join(" ", row.Message, row.Exception, row.PropertiesJson);
            all.Should().NotContain("wrong-pass-123").And.NotContain(TestUser.Password).And.NotContain(token)
                .And.NotContain(TestUser.PasswordHash).And.NotContain("development-only-");
        }
    }

    private async Task<(int, string)> RestoreStatusAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/database/restore") { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-auth-token", token);
        using var response = await _client.SendAsync(request, Cancellation);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation));
    }

    private async Task<string> LoginAsync(TestUser user)
    {
        using var response = await PostJsonAsync(LoginUri, JsonSerializer.Serialize(new { email = user.Email, password = TestUser.Password }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(Uri uri, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PostAsync(uri, content, Cancellation);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("x-auth-token", token);
        }

        return await _client.SendAsync(request, Cancellation);
    }

    private static JsonElement Payload(string token) =>
        JsonElement.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1])));

    private static string LegacyToken(string payload, string key)
    {
        var input = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." + payload;
        var signature = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(input));
        return input + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
    }

    private static async Task<JsonElement> RecordedStepAsync(string file, string id)
    {
        var steps = JsonElement.Parse(await File.ReadAllTextAsync(Fixtures.Characterization(file), Cancellation));
        var list = steps.ValueKind == JsonValueKind.Array ? steps : steps.GetProperty("steps");
        return list.EnumerateArray().Single(step => step.GetProperty("id").GetString() == id);
    }

    /// <summary>The app with a short user cache, so the role-removal test does not wait 30 s.</summary>
    public sealed class App : AppWithMongo
    {
        public static readonly TimeSpan UserCacheDuration = TimeSpan.FromMilliseconds(300);

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Auth:UserCacheDuration", UserCacheDuration.ToString());
    }
}
