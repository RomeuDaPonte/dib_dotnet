using System.Net;
using System.Text.Json;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Pages.Landing;
using Dib4t.Contracts.Jobs;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Pages;

/// <summary>The readings page (legacy <c>components/leituras/leiturasInex.jsx</c>, PLAN.md §7.4) against a substituted job API and clock.</summary>
public sealed class ReadingsPageTests : IAsyncDisposable
{
    private readonly ClientTestContext _context = new();
    private readonly IJobApi _jobs = Substitute.For<IJobApi>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    public ReadingsPageTests()
    {
        _context.Services.AddSingleton(_jobs);
        _context.Services.AddSingleton<TimeProvider>(_time);
        Answer(JobStates.Billed);
    }

    private ISnackbar Snackbar => _context.Services.GetRequiredService<ISnackbar>();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Theory]
    [InlineData(JobStates.Billed, "readings-state--billed")]
    [InlineData(JobStates.ForInvoicing, "readings-state--billed")]
    [InlineData(JobStates.Development, "readings-state--not-billed")]
    [InlineData(JobStates.OnApproval, "readings-state--not-billed")]
    [InlineData(JobStates.Approved, "readings-state--not-billed")]
    [InlineData(JobStates.Machining, "readings-state--not-billed")]
    [InlineData(JobStates.Assembly, "readings-state--not-billed")]
    [InlineData(JobStates.Deleted, "readings-state--not-billed")]
    public void Red_for_billed_or_for_invoicing_green_otherwise(string state, string modifier) =>
        Readings.StateClass(state).Split(' ').Should().Equal("readings-state", modifier);

    [Fact]
    public async Task A_scan_shows_the_state_clears_the_field_and_closes_after_3_seconds()
    {
        Answer(JobStates.ForInvoicing);
        var page = await RenderAsync("canSeeLeituras");

        await ScanAsync(page, "26-0007");

        await _jobs.Received(1).FindByBudgetNumberAsync("26-0007");
        _dialogs.WaitForAssertion(() => State().TextContent.Should().Be(JobStates.ForInvoicing));
        State().ClassList.Should().Contain("readings-state--billed");
        _dialogs.Find(".mud-dialog").GetAttribute("aria-label").Should().Be("Estado da obra");
        page.Find("#readings-budget-number").GetAttribute("value").Should().BeEmpty("the field is cleared for the next scan");

        _time.Advance(Readings.CloseAfter - TimeSpan.FromMilliseconds(1));
        _dialogs.FindAll("[data-testid=readings-state]").Should().ContainSingle("still open before 3 s");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[data-testid=readings-state]").Should().BeEmpty("closed after 3 s"));
        _context.JSInterop.Invocations.Should().Contain(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase), "the field gets the focus back");
        Snackbar.ShownSnackbars.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dialog_opens_empty_and_green_before_the_answer()
    {
        var pending = new TaskCompletionSource<ApiResult<JobResponse<BudgetRefsResponse>?>>();
        _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(pending.Task);
        var page = await RenderAsync("canSeeLeituras");

        var scan = ScanAsync(page, "26-0007");

        _dialogs.WaitForAssertion(() => State().TextContent.Should().BeEmpty());
        State().ClassList.Should().Contain("readings-state--not-billed");
        page.Find("#readings-budget-number").GetAttribute("value").Should().Be("26-0007");
        pending.SetResult(Job(JobStates.Machining));
        await scan;
        _dialogs.WaitForAssertion(() => State().TextContent.Should().Be(JobStates.Machining));
    }

    [Fact]
    public async Task A_number_without_a_job_shows_an_empty_green_state_and_closes_as_well()
    {
        _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(new ApiResult<JobResponse<BudgetRefsResponse>?>(null, HttpStatusCode.OK, null));
        var page = await RenderAsync("canSeeLeituras");

        await ScanAsync(page, "26-9999");

        _dialogs.WaitForAssertion(() => State().ClassList.Should().Contain("readings-state--not-billed"));
        State().TextContent.Should().BeEmpty();
        page.Find("#readings-budget-number").GetAttribute("value").Should().BeEmpty();
        _time.Advance(Readings.CloseAfter);
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[data-testid=readings-state]").Should().BeEmpty());
        Snackbar.ShownSnackbars.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task A_failure_toasts_and_leaves_the_dialog_open_and_the_field_as_typed(HttpStatusCode? status)
    {
        _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(ApiResult.Failure<JobResponse<BudgetRefsResponse>?>(status, "x"));
        var page = await RenderAsync("canSeeLeituras");

        await ScanAsync(page, "26-0007");

        _dialogs.WaitForAssertion(() => Snackbar.ShownSnackbars.Should().ContainSingle(s => s.Message == Readings.FailedMessage && s.Severity == Severity.Error));
        State().TextContent.Should().BeEmpty();
        page.Find("#readings-budget-number").GetAttribute("value").Should().Be("26-0007");
        _time.Advance(TimeSpan.FromMinutes(1));
        _dialogs.FindAll("[data-testid=readings-state]").Should().ContainSingle("only Sair closes it");

        await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sair").ClickAsync(new MouseEventArgs());
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[data-testid=readings-state]").Should().BeEmpty());
    }

    [Fact]
    public async Task An_empty_field_asks_the_empty_number_as_legacy()
    {
        _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(ApiResult.Failure<JobResponse<BudgetRefsResponse>?>(HttpStatusCode.BadRequest, "x"));
        var page = await RenderAsync("canSeeLeituras");

        await page.Find("#readings-budget-number").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        await _jobs.Received(1).FindByBudgetNumberAsync(string.Empty);
    }

    [Fact]
    public async Task Other_keys_ask_nothing()
    {
        var page = await RenderAsync("canSeeLeituras");

        await page.Find("#readings-budget-number").InputAsync(new ChangeEventArgs { Value = "26-0007" });
        await page.Find("#readings-budget-number").KeyDownAsync(new KeyboardEventArgs { Key = "7" });

        _jobs.ReceivedCalls().Should().BeEmpty();
        _dialogs.FindAll("[data-testid=readings-state]").Should().BeEmpty();
    }

    /// <summary>Legacy's first timer would close the second scan's dialog early (after "Sair"); here each scan has its 3 s.</summary>
    [Fact]
    public async Task A_new_scan_gets_its_own_3_seconds()
    {
        var page = await RenderAsync("canSeeLeituras");
        await ScanAsync(page, "26-0001");
        _dialogs.WaitForAssertion(() => State().TextContent.Should().Be(JobStates.Billed));
        await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sair").ClickAsync(new MouseEventArgs());
        _time.Advance(TimeSpan.FromSeconds(2));

        Answer(JobStates.Assembly);
        await ScanAsync(page, "26-0002");
        _dialogs.WaitForAssertion(() => State().TextContent.Should().Be(JobStates.Assembly));
        _time.Advance(TimeSpan.FromSeconds(2));
        _dialogs.FindAll("[data-testid=readings-state]").Should().ContainSingle("the first scan's timer no longer closes it");
        _time.Advance(TimeSpan.FromSeconds(1));
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[data-testid=readings-state]").Should().BeEmpty());
    }

    [Fact]
    public async Task Escape_and_the_backdrop_do_not_close_it()
    {
        var page = await RenderAsync("canSeeLeituras");
        await ScanAsync(page, "26-0001");
        _dialogs.WaitForAssertion(() => State());

        var dialog = _dialogs.FindComponent<MudDialog>().Instance;
        dialog.Options!.CloseOnEscapeKey.Should().BeFalse();
        dialog.Options.BackdropClick.Should().BeFalse();
        (dialog.Options.MaxWidth, dialog.Options.FullWidth).Should().Be((MaxWidth.ExtraLarge, true), "legacy fullWidth maxWidth=\"xl\"");
        dialog.Options.DefaultFocus.Should().Be(DefaultFocus.Element, "a scanner's Enter must not press Sair");
    }

    [Fact]
    public async Task The_field_is_the_legacy_one()
    {
        var page = await RenderAsync("canSeeLeituras");

        var field = page.FindComponent<MudTextField<string>>().Instance;
        (field.Label, field.AutoFocus).Should().Be(("N Orçamento", true));
    }

    [Fact]
    public async Task Without_the_readings_role_the_page_asks_nothing()
    {
        var page = await RenderAsync("canSeeObras");

        page.Markup.Should().Contain(Dib4t.SharedKernel.Auth.AuthTexts.NoPermission);
        page.FindAll("#readings-budget-number").Should().BeEmpty();
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    private AngleSharp.Dom.IElement State() => _dialogs.Find("[data-testid=readings-state]");

    private static async Task ScanAsync(IRenderedComponent<Readings> page, string number)
    {
        await page.Find("#readings-budget-number").InputAsync(new ChangeEventArgs { Value = number });
        await page.Find("#readings-budget-number").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
    }

    private void Answer(string state) => _jobs.FindByBudgetNumberAsync(Arg.Any<string>()).Returns(Job(state));

    private static ApiResult<JobResponse<BudgetRefsResponse>?> Job(string state) =>
        ApiResult.Success<JobResponse<BudgetRefsResponse>?>(JsonSerializer.Deserialize<JobResponse<BudgetRefsResponse>>(
            $$"""{"finalPrice":100,"state":"{{state}}","orderIdentification":"","commentType":[],"_id":"6a1f00000000000000000001"}""", JsonSerializerOptions.Web));

    private async Task<IRenderedComponent<Readings>> RenderAsync(string role)
    {
        _context.StorageModule.Setup<string?>("getToken").SetResult(ClientTestContext.Token("Ana Administradora", role));
        await _context.Session.GetAuthenticationStateAsync();
        _context.Render<MudPopoverProvider>();
        _dialogs = _context.Render<MudDialogProvider>();
        return _context.Render<Readings>();
    }
}
