using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Pages.Landing;
using Dib4t.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Pages;

/// <summary>The dashboard (legacy <c>components/dashboard/dashboardIndex.jsx</c>) against a substituted job API.</summary>
public sealed class DashboardPageTests : IAsyncDisposable
{
    private readonly ClientTestContext _context = new();
    private readonly IJobApi _jobs = Substitute.For<IJobApi>();

    public DashboardPageTests()
    {
        _context.Services.AddSingleton(_jobs);
        // 31 December 23:30 UTC is already 2027 in Madrid: the year is the browser's local one (legacy getFullYear()).
        _context.Services.AddSingleton<TimeProvider>(new FixedTime(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid")));
        _jobs.ValidTryOnFirstTestCountersAsync(Arg.Any<int>()).Returns(ApiResult.Success(new ValidTryCountersResponse(7, 2, 1)));
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task The_pie_has_the_legacy_slices_order_and_colors_for_the_local_year()
    {
        var page = await RenderAsync("canSeeDashboard");

        page.WaitForAssertion(() => page.Find("[data-testid=dashboard-chart]").GetAttribute("data-counters").Should().Be("1,2,7", "3º, 2º, 1º teste"));
        await _jobs.Received(1).ValidTryOnFirstTestCountersAsync(2027);
        Dashboard.Labels.Should().Equal("3º teste", "2º teste", "1º teste");
        Dashboard.Palette.Should().Equal("#ff0000", "#ffa500", "#008000");
        var chart = page.FindComponent<MudChart<double>>().Instance;
        (chart.ChartType, chart.LegendPosition).Should().Be((ChartType.Pie, Position.Right));
        chart.ChartLabels.Should().Equal(Dashboard.Labels);
        chart.ChartSeries.Should().ContainSingle().Which.Data.Values.Should().Equal(1, 2, 7);
        chart.ChartOptions!.ChartPalette.Should().Equal(Dashboard.Palette);
    }

    [Fact]
    public async Task Only_zeros_draw_nothing_as_google_charts()
    {
        _jobs.ValidTryOnFirstTestCountersAsync(Arg.Any<int>()).Returns(ApiResult.Success(new ValidTryCountersResponse(0, 0, 0)));

        var page = await RenderAsync("canSeeDashboard");

        page.WaitForAssertion(() => page.Find("[data-testid=dashboard-chart]").GetAttribute("data-counters").Should().Be("0,0,0"));
        page.FindComponents<MudChart<double>>().Should().BeEmpty();
        page.Find("[data-testid=dashboard-chart]").TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_shown_while_loading_or_after_a_failure()
    {
        _jobs.ValidTryOnFirstTestCountersAsync(Arg.Any<int>()).Returns(ApiResult.Failure<ValidTryCountersResponse>(HttpStatusCode.InternalServerError, "Something failed."));

        var page = await RenderAsync("canSeeDashboard");

        page.WaitForAssertion(() => _jobs.ReceivedCalls().Should().ContainSingle());
        page.FindAll("[data-testid=dashboard-chart]").Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_dashboard_role_the_page_asks_nothing()
    {
        var page = await RenderAsync("canSeeObras");

        page.Markup.Should().Contain(Dib4t.SharedKernel.Auth.AuthTexts.NoPermission);
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    private async Task<IRenderedComponent<Dashboard>> RenderAsync(string role)
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", role));
        await _context.Session.GetAuthenticationStateAsync();
        return _context.Render<Dashboard>();
    }

    private sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
