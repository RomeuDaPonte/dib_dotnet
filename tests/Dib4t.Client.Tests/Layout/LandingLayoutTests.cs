using Bunit;
using Dib4t.Client.Layout;
using Dib4t.Client.Navigation;
using Dib4t.Client.Pages.Landing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace Dib4t.Client.Tests.Layout;

/// <summary>The landing shell (legacy landing.jsx + menuListItem.jsx, PLAN.md §8.3, §8.4).</summary>
public sealed class LandingLayoutTests
{
    [Fact]
    public async Task The_menu_has_links_with_the_legacy_icons_in_the_legacy_order()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeDefinicoes", "canSeeDashboard", "canSeeObras"));

        var shell = RenderShell(context);

        shell.WaitForAssertion(() => shell.FindAll("nav[aria-label=Menu] a").Should().HaveCount(3));
        var links = shell.FindAll("nav[aria-label=Menu] a");
        links.Select(a => (a.TextContent, a.GetAttribute("href"))).Should().Equal(
            ("Dashboard", "landing/dashboard"), ("Obras", "landing/job"), ("Definições", "landing/settings"));
        links.Select(a => a.QuerySelector("svg path:last-child")!.GetAttribute("d")).Should().Equal(
            new[] { Icons.Material.Filled.ShowChart, Icons.Material.Filled.Build, Icons.Material.Filled.Settings }
                .Select(LastPath));
    }

    [Fact]
    public void Every_menu_item_has_its_legacy_icon()
    {
        MenuItems.All.Select(item => (item.Label, item.Icon)).Should().Equal(
            ("Dashboard", Icons.Material.Filled.ShowChart),
            ("Orçamentos", Icons.Material.TwoTone.Description),
            ("Obras", Icons.Material.Filled.Build),
            ("Adjudicações", Icons.Material.Filled.History),
            ("Entidades", Icons.Material.Filled.People),
            ("Leituras", Icons.Material.Filled.QrCode),
            ("Definições", Icons.Material.Filled.Settings));
    }

    [Theory]
    [InlineData(Breakpoint.Xs, LandingLayout.ShellMode.Overlay)]
    [InlineData(Breakpoint.Sm, LandingLayout.ShellMode.Overlay)]
    [InlineData(Breakpoint.Md, LandingLayout.ShellMode.Mini)]
    [InlineData(Breakpoint.Lg, LandingLayout.ShellMode.Open)]
    [InlineData(Breakpoint.Xl, LandingLayout.ShellMode.Open)]
    [InlineData(Breakpoint.Xxl, LandingLayout.ShellMode.Open)]
    public void The_drawer_mode_follows_the_breakpoint(Breakpoint breakpoint, LandingLayout.ShellMode mode) =>
        LandingLayout.ModeFor(breakpoint).Should().Be(mode);

    [Fact]
    public async Task The_menu_button_opens_the_drawer_and_the_chevron_closes_it()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeObras"));
        var shell = RenderShell(context);
        shell.WaitForAssertion(() => shell.Find("button[aria-label='Abrir menu']"));

        await shell.Find("button[aria-label='Abrir menu']").ClickAsync(new());

        shell.Find("#landing-drawer").ClassList.Should().Contain("mud-drawer--open");
        shell.FindAll("button[aria-label='Abrir menu']").Should().BeEmpty("legacy hides it while the drawer is open");

        await shell.Find("button[aria-label='Fechar menu']").ClickAsync(new());

        shell.Find("#landing-drawer").ClassList.Should().Contain("mud-drawer--closed");
        shell.FindAll("button[aria-label='Fechar menu']").Should().BeEmpty();
    }

    [Fact]
    public async Task The_theme_switch_turns_the_light_palette_on_and_the_next_shell_starts_dark()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeObras"));
        var shell = RenderShell(context);
        shell.WaitForAssertion(() => shell.Find("input[role=switch]"));
        Background(shell).Should().Be(Dark, "dark is the default (§8.3)");

        await shell.Find("input[role=switch]").ChangeAsync(new() { Value = true });

        context.Theme.IsLight.Should().BeTrue();
        shell.WaitForAssertion(() => Background(shell).Should().Be(Light));

        await context.DisposeComponentsAsync(); // the router leaves the landing shell (logout → login page)

        context.Theme.IsLight.Should().BeFalse("the login page is always dark and the next shell starts dark");
    }

    [Fact]
    public async Task The_switch_has_an_accessible_name()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeObras"));

        var shell = RenderShell(context);

        shell.WaitForAssertion(() => shell.Find("input[role=switch]"));
        var labelId = shell.Find("input[role=switch]").GetAttribute("aria-labelledby");
        shell.Find($"#{labelId}").TextContent.Should().Be("Tema claro");
    }

    private const string Dark = "#303030";
    private const string Light = "#fafafa";

    /// <summary>The background color the theme provider's style sheet sets now (dark or light palette).</summary>
    private static string Background(IRenderedComponent<IComponent> shell)
    {
        var css = string.Concat(shell.FindAll("style").Select(style => style.TextContent));
        var start = css.IndexOf("--mud-palette-background:", StringComparison.Ordinal) + "--mud-palette-background:".Length;
        var value = css[start..css.IndexOf(';', start)].Trim();
        return value.StartsWith("rgba(48,48,48", StringComparison.Ordinal) ? Dark
            : value.StartsWith("rgba(250,250,250", StringComparison.Ordinal) ? Light
            : value;
    }

    /// <summary>The icon's drawing (its last path; the first is the transparent 24×24 box).</summary>
    private static string LastPath(string icon)
    {
        var start = icon.LastIndexOf(" d=\"", StringComparison.Ordinal) + 4;
        return icon[start..icon.IndexOf('"', start)];
    }

    /// <summary>MainLayout (theme provider) → LandingLayout → Home, as the router nests them.</summary>
    private static IRenderedComponent<IComponent> RenderShell(ClientTestContext context) =>
        context.Render(builder =>
        {
            builder.OpenComponent<CascadingAuthenticationState>(0);
            builder.AddComponentParameter(1, nameof(CascadingAuthenticationState.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<MainLayout>(0);
                child.AddComponentParameter(1, nameof(LayoutComponentBase.Body), (RenderFragment)(main =>
                {
                    main.OpenComponent<LandingLayout>(0);
                    main.AddComponentParameter(1, nameof(LayoutComponentBase.Body), (RenderFragment)(body =>
                    {
                        body.OpenComponent<Home>(0);
                        body.CloseComponent();
                    }));
                    main.CloseComponent();
                }));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        });
}
