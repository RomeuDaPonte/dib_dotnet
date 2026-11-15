using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dib4t.Tests.Common;

namespace Dib4t.ImportCompatibility.Tests;

/// <summary>
/// Phase 1 exit (PLAN.md §4.0 item 3, §10): after a legacy backup is imported, an existing user logs in to the new
/// app with their current password (the bcryptjs hash from the legacy app), and the token opens the app with that
/// user's name and roles as stored.
/// </summary>
public sealed class ImportedUsersLoginTests(AppWithMongo app) : IClassFixture<AppWithMongo>
{
    /// <summary>The one password tools/legacy/dibbak.cs anonymize gives every user of the fixture.</summary>
    private const string FixturePassword = "dib4t-dev";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_user_of_the_production_backup_logs_in_with_the_imported_password()
    {
        var seed = DibbakFile.Read(Fixtures.ProductionBackup).ToSnapshot();
        await RestoreAsync(await File.ReadAllBytesAsync(Fixtures.ProductionBackup, Cancellation));
        using var client = app.Factory.CreateClient();
        var users = seed.Collections["users"];
        users.Should().HaveCount(10);

        foreach (var user in users)
        {
            var email = user["email"].AsString;
            using var login = await client.PostAsync(new Uri("/api/account/login", UriKind.Relative),
                new StringContent(JsonSerializer.Serialize(new { email, password = FixturePassword }), Encoding.UTF8, "application/json"), Cancellation);
            var token = await login.Content.ReadAsStringAsync(Cancellation);
            login.StatusCode.Should().Be(HttpStatusCode.OK, "{0} logs in with the imported hash ({1})", email, token);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/me");
            request.Headers.Add("x-auth-token", token);
            using var me = await client.SendAsync(request, Cancellation);
            var body = JsonElement.Parse(await me.Content.ReadAsStringAsync(Cancellation));

            me.StatusCode.Should().Be(HttpStatusCode.OK);
            body.GetProperty("_id").GetString().Should().Be(user["_id"].AsObjectId.ToString(), "imports keep the _id, so tokens stay valid");
            body.GetProperty("name").GetString().Should().Be(user["name"].AsString);
            body.GetProperty("roles").EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null : r.GetString())
                .Should().Equal(user["roles"].AsBsonArray.Select(r => r.IsBsonNull ? null : r.AsString));
        }
    }

    private async Task RestoreAsync(byte[] backup)
    {
        using var client = app.CreateClient(TestUser.Admin);
        using var content = new ByteArrayContent(backup);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync(new Uri("/api/database/restore", UriKind.Relative), content, Cancellation);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
