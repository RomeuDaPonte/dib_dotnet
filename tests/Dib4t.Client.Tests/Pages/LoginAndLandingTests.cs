using System.Net;
using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Layout;
using Dib4t.Client.Pages;
using Dib4t.Client.Pages.Landing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;

namespace Dib4t.Client.Tests.Pages;

/// <summary>Login page (legacy login.jsx) and the /landing/* login gate.</summary>
public sealed class LoginAndLandingTests
{
    [Fact]
    public async Task Login_page_shows_the_legacy_form_without_the_remember_checkbox()
    {
        await using var context = new ClientTestContext();

        var page = RenderLogin(context);

        page.Find("h1").TextContent.Should().Be("Sign in");
        page.Find("#email").Should().NotBeNull();
        page.Find("#password").GetAttribute("type").Should().Be("password");
        page.Markup.Should().NotContain("Lembrar", "D14: the login is always remembered");
    }

    [Fact]
    public async Task Submitting_an_empty_form_shows_the_yup_messages_and_sends_nothing()
    {
        await using var context = new ClientTestContext();
        var page = RenderLogin(context);

        await page.Find("form").SubmitAsync();

        page.Markup.Should().Contain("Email is a required field").And.Contain("password is a required field");
        await context.Account.DidNotReceiveWithAnyArgs().LoginAsync(default!, default!);
    }

    [Fact]
    public async Task A_successful_login_goes_to_the_landing_home()
    {
        await using var context = new ClientTestContext();
        context.Account.LoginAsync("ana.admin@dib4t.local", "char-test-1").Returns(ApiResult.Success(ClientTestContext.Token("Ana")));
        var page = RenderLogin(context);

        await page.Find("#email").InputAsync(new() { Value = "ana.admin@dib4t.local" });
        await page.Find("#password").InputAsync(new() { Value = "char-test-1" });
        await page.Find("form").SubmitAsync();

        page.WaitForAssertion(() => context.Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/landing/home"));
    }

    [Fact]
    public async Task A_rejected_login_shows_the_server_text_as_an_error_notification()
    {
        await using var context = new ClientTestContext();
        context.Account.LoginAsync("ana.admin@dib4t.local", "wrong-pass").Returns(ApiResult.Failure<string>(HttpStatusCode.BadRequest, "Password incorreta!"));
        var page = RenderLogin(context);

        await page.Find("#email").InputAsync(new() { Value = "ana.admin@dib4t.local" });
        await page.Find("#password").InputAsync(new() { Value = "wrong-pass" });
        await page.Find("form").SubmitAsync();

        page.WaitForAssertion(() => context.Services.GetRequiredService<ISnackbar>().ShownSnackbars
            .Should().ContainSingle(s => s.Message == "Password incorreta!" && s.Severity == Severity.Error));
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/");
    }

    [Fact]
    public async Task An_open_session_skips_the_login_page()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana"));

        RenderLogin(context);

        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/landing/home");
    }

    [Fact]
    public async Task Landing_without_a_login_goes_to_the_login_page()
    {
        await using var context = new ClientTestContext();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("landing/home");

        RenderLanding(context);

        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/");
    }

    [Fact]
    public async Task Landing_shows_the_name_the_menu_of_the_roles_and_logs_out()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana Administradora", "canSeeObras", null, "canSeeDefinicoes"));
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("landing/home");
        var landing = RenderLanding(context);

        landing.WaitForAssertion(() => landing.Find("header h1").TextContent.Should().Be("Ana Administradora"));
        landing.FindAll("nav li").Select(li => li.TextContent).Should().Equal("Obras", "Definições");
        landing.Markup.Should().Contain("Home component");

        await landing.Find("button[aria-label=Sair]").ClickAsync(new());

        context.Invocations("removeToken").Should().Be(1);
        landing.WaitForAssertion(() => navigation.Uri.Should().Be("http://localhost/"));
    }

    private static IRenderedComponent<IComponent> RenderLogin(ClientTestContext context) =>
        context.Render(builder =>
        {
            builder.OpenComponent<CascadingAuthenticationState>(0);
            builder.AddComponentParameter(1, nameof(CascadingAuthenticationState.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<MudSnackbarProvider>(0);
                child.CloseComponent();
                child.OpenComponent<Login>(1);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        });

    private static IRenderedComponent<IComponent> RenderLanding(ClientTestContext context) =>
        context.Render(builder =>
        {
            builder.OpenComponent<CascadingAuthenticationState>(0);
            builder.AddComponentParameter(1, nameof(CascadingAuthenticationState.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<MudPopoverProvider>(0);
                child.CloseComponent();
                child.OpenComponent<LandingLayout>(1);
                child.AddComponentParameter(2, nameof(LayoutComponentBase.Body), (RenderFragment)(body =>
                {
                    body.OpenComponent<Home>(0);
                    body.CloseComponent();
                }));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        });
}
