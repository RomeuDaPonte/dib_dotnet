using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dib4t.Server.Tests;

/// <summary>The server hosts the WebAssembly client (PLAN.md §2, §3).</summary>
public sealed class HostingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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
}
