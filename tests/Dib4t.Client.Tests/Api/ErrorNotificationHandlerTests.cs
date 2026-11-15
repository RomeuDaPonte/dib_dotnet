using System.Net;
using Dib4t.Client.Api;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Api;

/// <summary>Global HTTP error handling: legacy httpService.js axios interceptor (PLAN.md §8.2).</summary>
public sealed class ErrorNotificationHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task An_answer_that_is_neither_a_success_nor_4xx_shows_the_legacy_notification(HttpStatusCode status)
    {
        var (client, snackbar) = Client(_ => new HttpResponseMessage(status) { Content = new StringContent("Something failed.") });

        using var response = await client.GetAsync(new Uri("api/budget", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(status, "the answer is passed on unchanged");
        snackbar.Received(1).Add("An anexpected error occurred", Severity.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData((HttpStatusCode)499)]
    public async Task Successes_and_4xx_are_left_to_the_caller(HttpStatusCode status)
    {
        var (client, snackbar) = Client(_ => new HttpResponseMessage(status));

        using var response = await client.GetAsync(new Uri("api/budget", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(status);
        snackbar.DidNotReceiveWithAnyArgs().Add(default(string)!);
    }

    [Fact]
    public async Task A_network_error_shows_the_notification_and_is_rethrown()
    {
        var (client, snackbar) = Client(_ => throw new HttpRequestException("connection refused"));

        var send = () => client.GetAsync(new Uri("api/budget", UriKind.Relative), TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<HttpRequestException>();
        snackbar.Received(1).Add("An anexpected error occurred", Severity.Error);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(200, false)]
    [InlineData(299, false)]
    [InlineData(304, true)]
    [InlineData(400, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public void IsUnexpected_matches_the_legacy_espectedError_rule(int? status, bool unexpected) =>
        ErrorNotificationHandler.IsUnexpected(status).Should().Be(unexpected);

    [Fact]
    public void An_api_result_of_a_5xx_or_a_network_error_is_unexpected()
    {
        ApiResult.Failure<string>(HttpStatusCode.InternalServerError, AccountApi.UnexpectedError).IsUnexpected.Should().BeTrue();
        ApiResult.Failure<string>(null, AccountApi.UnexpectedError).IsUnexpected.Should().BeTrue();
        ApiResult.Failure<string>(HttpStatusCode.BadRequest, "Password incorreta!").IsUnexpected.Should().BeFalse();
        ApiResult.Success("token").IsUnexpected.Should().BeFalse();
    }

    private static (HttpClient Client, ISnackbar Snackbar) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var snackbar = Substitute.For<ISnackbar>();
#pragma warning disable CA2000 // the HttpClient disposes the handler chain
        var handler = new ErrorNotificationHandler(snackbar) { InnerHandler = new StubHandler(answer) };
#pragma warning restore CA2000
        return (new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, snackbar);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
