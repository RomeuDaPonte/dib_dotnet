using System.Net;
using Dib4t.Client.Api;

namespace Dib4t.Client.Tests.Auth;

/// <summary>Every request carries x-auth-token; only a 400 "Invalid token." logs out (PLAN.md §4.6).</summary>
public sealed class AuthHeaderHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Invalid token.", true)]
    [InlineData(HttpStatusCode.BadRequest, "Password incorreta!", false)]
    [InlineData(HttpStatusCode.Unauthorized, "Não tem permissão para concluir esta acção.", false)]
    [InlineData(HttpStatusCode.InternalServerError, "Something failed.", false)]
    public async Task Sends_the_token_and_logs_out_only_on_an_invalid_token(HttpStatusCode status, string body, bool loggedOut)
    {
        await using var context = new ClientTestContext("stored-token");
        string? sentToken = null;
        using var handler = new AuthHeaderHandler(context.Tokens)
        {
            InnerHandler = new Responder(request =>
            {
                sentToken = request.Headers.GetValues("x-auth-token").Single();
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            }),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var response = await client.GetAsync(new Uri("api/account/me", UriKind.Relative), TestContext.Current.CancellationToken);

        sentToken.Should().Be("stored-token");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(body, "the caller still reads the body");
        context.Tokens.Current.Should().Be(loggedOut ? null : "stored-token");
    }

    [Fact]
    public async Task Without_a_token_no_header_is_sent()
    {
        await using var context = new ClientTestContext();
        var hasHeader = true;
        using var handler = new AuthHeaderHandler(context.Tokens)
        {
            InnerHandler = new Responder(request =>
            {
                hasHeader = request.Headers.Contains("x-auth-token");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var response = await client.GetAsync(new Uri("api/account/login", UriKind.Relative), TestContext.Current.CancellationToken);

        hasHeader.Should().BeFalse();
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
