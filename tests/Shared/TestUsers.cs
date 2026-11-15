using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Dib4t.SharedKernel.Auth;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Tests.Common;

/// <summary>
/// Users for API tests, stored in the legacy shape (PLAN.md §4.2) with a bcrypt hash made by the legacy bcryptjs
/// (cost 4, from the auth golden file, so logins stay fast). Names and e-mails are the ones of the recorded legacy
/// characterization (<c>01-account.json</c>).
/// </summary>
public sealed record TestUser(ObjectId Id, string Name, string Email, string[] Roles, string FieldOfCompetencies = "Desenvolvimento")
{
    /// <summary>The password of every test user (as in the characterization recording).</summary>
    public const string Password = "char-test-1";

    /// <summary>Every role (legacy <c>getAllRoles</c>).</summary>
    public static TestUser Admin { get; } = new(ObjectId.Parse("5f1e9a2b3c4d5e6f70819203"), "Ana Administradora", "ana.admin@dib4t.local",
        ["canSeeOrcamentos", "canSeeObras", "canSeeHistorico", "canSeeEntidades", "canSeeDefinicoes", "canSeeDashboard", "canSeeLeituras"]);

    /// <summary>No settings role.</summary>
    public static TestUser Limited { get; } = new(ObjectId.Parse("5f1e9a2b3c4d5e6f70819204"), "Bruno Maquinação", "bruno.limitado@dib4t.local",
        ["canSeeObras", "canSeeLeituras"], "Maquinação");

    /// <summary>bcryptjs <c>hashSync("char-test-1", "$2a$04$…")</c>.</summary>
    public static string PasswordHash { get; } = JsonElement.Parse(File.ReadAllText(Fixtures.AuthGolden)).GetProperty("bcrypt")
        .EnumerateArray().First(c => c.GetProperty("password").GetString() == Password && c.GetProperty("hash").GetString()!.StartsWith("$2a$04$", StringComparison.Ordinal))
        .GetProperty("hash").GetString()!;

    /// <summary>The legacy insert field order (<c>POST /api/account/create</c>).</summary>
    public BsonDocument ToDocument() => new()
    {
        { "_id", Id },
        { "roles", new BsonArray(Roles) },
        { "fieldOfCompetencies", FieldOfCompetencies },
        { "name", Name },
        { "email", Email },
        { "password", PasswordHash },
        { "__v", 0 },
    };

    /// <summary>Inserts or replaces the user (a restore may have removed it).</summary>
    public Task SaveAsync(IMongoDatabase database, CancellationToken cancellationToken) =>
        database.GetCollection<BsonDocument>("users").ReplaceOneAsync(
            new BsonDocument("_id", Id), ToDocument(), new ReplaceOptions { IsUpsert = true }, cancellationToken);

    /// <summary>A token from the app's own token service (what a login returns).</summary>
    public string TokenFrom(IServiceProvider services) =>
        services.GetRequiredService<ITokenService>().CreateToken(new User(Id.ToString(), Name, Email, PasswordHash, Roles, FieldOfCompetencies));
}

/// <summary>Signs every request as <paramref name="user"/>, saving the user first so it exists even after a restore.</summary>
public sealed class TestUserHandler(TestUser user, IMongoDatabase database, string token) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await user.SaveAsync(database, cancellationToken);
        request.Headers.Remove(AuthTexts.TokenHeader);
        request.Headers.Add(AuthTexts.TokenHeader, token);
        return await base.SendAsync(request, cancellationToken);
    }
}
