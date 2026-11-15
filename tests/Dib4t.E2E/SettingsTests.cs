using Dib4t.E2E.Harness;
using Dib4t.E2E.Pages.Blazor;
using Dib4t.E2E.Pages.Legacy;
using Microsoft.Playwright;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Dib4t.E2E;

/// <summary>
/// Settings (PLAN.md §7.2, §10 Phase 2): the same changes made through the settings page of both UIs, each app on its
/// own database loaded from the same seed backup, show the same messages and leave the same documents (BSON types
/// and field order included; only the new documents' ids and the password salts differ).
/// </summary>
public sealed class SettingsTests(E2EHarness harness) : E2ETest(harness)
{
    private const string FixtureUser = "utilizador02@dib4t.local";
    private const string FixturePassword = "dib4t-dev";
    private const string NewUserEmail = "eva.teste@dib4t.local";
    private const string NewPaymentMethod = "Transferência a 45 dias";
    private const string NewPrinterIp = "127.0.0.9";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    [Fact]
    public async Task Settings_changes_in_both_uis_show_the_same_messages_and_leave_the_same_documents()
    {
        await Harness.LoadSeedAsync(Cancellation);
        try
        {
            var results = new List<(string App, IReadOnlyList<string> Messages, string Price, DbSnapshot Snapshot)>();
            foreach (var app in Harness.Apps)
            {
                var session = await OpenAsync(app);
                ISettingsPage settings = app == Harness.Legacy ? new LegacySettingsPage(session.Page) : new NewSettingsPage(session.Page);
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

                await settings.GotoAsync();
                var messages = new List<string>
                {
                    await AfterAsync(settings, () => settings.CreateUserAsync("Eva Teste", NewUserEmail, "eva-pass-1", "Obras", "Leituras")),
                    await AfterAsync(settings, () => settings.SetAutomationPriceAsync("32.5")),
                };
                var price = await settings.AutomationPriceAsync();
                await settings.AddPaymentMethodAsync(NewPaymentMethod);
                messages.Add(await AfterAsync(settings, () => settings.AddPrinterAsync(NewPrinterIp, "9100", "Produção")));
                results.Add((app.Name, messages, price, await app.SnapshotAsync(Cancellation)));
                session.Errors.Should().BeEmpty(app.Name);
            }

            var (legacy, created) = (results[0], results[1]);
            created.Messages.Should().Equal(legacy.Messages).And.Equal("Update bem sucedido!", "Preços atualizados com sucesso!", "Impressora guardada com sucesso!");
            (created.Price, legacy.Price).Should().Be(("32.5 €", "32.5 €"));
            New(created.Snapshot, "users", "email", NewUserEmail).Should().Be(New(legacy.Snapshot, "users", "email", NewUserEmail));
            New(created.Snapshot, "paymentmethods", "name", NewPaymentMethod).Should().Be(New(legacy.Snapshot, "paymentmethods", "name", NewPaymentMethod));
            New(created.Snapshot, "printersettings", "ip", NewPrinterIp).Should().Be(New(legacy.Snapshot, "printersettings", "ip", NewPrinterIp));
            Without(created.Snapshot).DiffFrom(Without(legacy.Snapshot)).Should().BeEmpty("every other document, prices included, is the same in both apps");
        }
        finally
        {
            await Harness.LoadSeedAsync(Cancellation); // back to the seed for the other tests
        }
    }

    /// <summary>The shell step's open item (§10 Phase 1): on a phone the overlay drawer closes after choosing a page.</summary>
    [Fact]
    public async Task On_a_phone_the_menu_closes_after_choosing_settings()
    {
        var session = await OpenAsync(Harness.New, new ViewportSize { Width = 360, Height = 640 });
        await new NewLoginPage(session.Page).GotoAsync();
        var shell = await new NewLoginPage(session.Page).LoginAsync(FixtureUser, FixturePassword);

        await shell.OpenMenu.ClickAsync();
        await shell.Menu.GetByText("Definições").ClickAsync();
        await session.Page.WaitForURLAsync("**/landing/settings");

        await session.Page.Locator(".settings-table tbody tr").First.WaitForAsync();
        await session.Page.WaitForFunctionAsync("() => getComputedStyle(document.getElementById('landing-drawer')).visibility === 'hidden'");
        (await shell.DrawerWidthAsync()).Should().Be(0);
    }

    /// <summary>Runs <paramref name="action"/> and returns the next notification's text.</summary>
    private static async Task<string> AfterAsync(ISettingsPage settings, Func<Task> action)
    {
        var before = await settings.Notification.CountAsync();
        await action();
        var next = settings.Notification.Nth(before);
        await next.WaitForAsync(new() { Timeout = 15_000 });
        return (await next.InnerTextAsync()).Trim();
    }

    /// <summary>The new document, without its <c>_id</c> and with the bcrypt hash reduced to its format.</summary>
    private static string New(DbSnapshot snapshot, string collection, string field, string value)
    {
        var document = snapshot.Collections[collection].Single(d => d.TryGetValue(field, out var v) && v == value).DeepClone().AsBsonDocument;
        document.Remove("_id");
        if (document.TryGetValue("password", out var hash))
        {
            hash.AsString.Should().MatchRegex(@"^\$2a\$10\$.{53}$");
            document["password"] = hash.AsString[..7];
        }

        return document.ToJson(Canonical);
    }

    private static DbSnapshot Without(DbSnapshot snapshot) => DbSnapshot.From(snapshot.Collections.ToDictionary(
        c => c.Key,
        c => c.Value.Where(d => !(c.Key == "users" && d["email"] == NewUserEmail)
                                && !(c.Key == "paymentmethods" && d["name"] == NewPaymentMethod)
                                && !(c.Key == "printersettings" && d["ip"] == NewPrinterIp))));
}
