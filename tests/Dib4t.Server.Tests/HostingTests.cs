using System.Net;
using Dib4t.Tests.Common.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dib4t.Server.Tests;

/// <summary>The server hosts the WebAssembly client (PLAN.md §2, §3).</summary>
public sealed class HostingTests(HostingTests.App factory) : IClassFixture<HostingTests.App>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Root_serves_the_WebAssembly_host_page_without_prerendering()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        html.Should().Contain("<html lang=\"pt\">").And.Contain("_framework/blazor.web");
        // Prerendering is off: the page content is rendered by the client only (§2, §4.6).
        html.Should().NotContain("<h1>");
    }

    [Fact]
    public async Task Blazor_framework_script_is_served()
    {
        var response = await _client.GetAsync(new Uri("/_framework/blazor.web.js", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_client_route_returns_404_with_the_host_page()
    {
        var response = await _client.GetAsync(new Uri("/landing/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Unknown_api_route_returns_404_without_html()
    {
        var response = await _client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().BeEmpty();
    }

    /// <summary>
    /// Without a database the app still starts, after waiting <c>Mongo:StartupSeedTimeout</c> for the startup seed
    /// (PLAN.md §7.2), which it then keeps trying in the background.
    /// </summary>
    [Fact]
    public async Task Starts_without_a_database_and_reports_the_seed_as_not_done()
    {
        var rows = await LogStoreFile.WaitForAsync(factory.LogStorePath,
            r => r.Any(x => x.Message.StartsWith("Default data (prices, administrator) not seeded", StringComparison.Ordinal)),
            cancellationToken: TestContext.Current.CancellationToken);

        rows.Should().Contain(r => r.Level == "Error" && r.Category == "Database");
    }

    /// <summary>The app without a database (nothing listens on port 1), with its own temporary log store.</summary>
    public sealed class App : WebApplicationFactory<Program>
    {
        private readonly string _logStorePath = LogStoreFile.NewTempPath();

        public string LogStorePath => _logStorePath;

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Logs:Path", _logStorePath)
            .UseSetting("Mongo:ConnectionString", "mongodb://127.0.0.1:1/dib4t?retryWrites=false&serverSelectionTimeoutMS=200&connectTimeoutMS=200")
            .UseSetting("Mongo:StartupSeedTimeout", "00:00:00.300");

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            LogStoreFile.Delete(_logStorePath);
        }
    }
}
