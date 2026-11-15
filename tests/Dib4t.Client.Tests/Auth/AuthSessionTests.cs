using System.Net;
using Dib4t.Client.Api;
using Dib4t.Client.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using NSubstitute;

namespace Dib4t.Client.Tests.Auth;

/// <summary>PLAN.md §4.6, §8.2: the login lives in localStorage, never expires, is refreshed from /me, ends only by logout.</summary>
public sealed class AuthSessionTests
{
    [Fact]
    public async Task Without_a_stored_token_the_user_is_logged_out()
    {
        await using var context = new ClientTestContext();

        var state = await context.Session.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
        await context.Account.DidNotReceive().GetMeAsync();
    }

    [Fact]
    public async Task A_stored_token_is_a_session_whose_name_and_roles_come_from_me()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Old Name", "canSeeObras"));
        context.Account.GetMeAsync().Returns(ApiResult.Success(new SessionUser("5f1e9a2b3c4d5e6f70819203", "New Name", "a@b.pt", ["canSeeDashboard"], null)));

        var state = await context.Session.GetAuthenticationStateAsync();

        state.User.Identity!.Name.Should().Be("New Name");
        state.User.IsInRole("canSeeDashboard").Should().BeTrue();
        state.User.IsInRole("canSeeObras").Should().BeFalse("a removed role disappears without a new login");
    }

    [Fact]
    public async Task When_me_cannot_be_reached_the_token_claims_stay_in_use()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeObras"));

        var state = await context.Session.GetAuthenticationStateAsync();

        state.User.Identity!.Name.Should().Be("Ana");
        context.Invocations("removeToken").Should().Be(0, "a server that is down does not log anyone out");
    }

    [Fact]
    public async Task An_unreadable_stored_token_is_removed()
    {
        await using var context = new ClientTestContext("garbage");

        var state = await context.Session.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
        context.Invocations("removeToken").Should().Be(1);
    }

    [Fact]
    public async Task Login_stores_the_token_and_announces_the_new_state()
    {
        await using var context = new ClientTestContext();
        var token = ClientTestContext.Token("Ana", "canSeeObras");
        context.Account.LoginAsync("ana.admin@dib4t.local", "char-test-1").Returns(ApiResult.Success(token));
        await context.Session.GetAuthenticationStateAsync();
        Task<AuthenticationState>? announced = null;
        context.Session.AuthenticationStateChanged += task => announced = task;

        var result = await context.Session.LoginAsync("ana.admin@dib4t.local", "char-test-1");

        result.IsSuccess.Should().BeTrue();
        context.StorageModule.Invocations.Should().Contain(i => i.Identifier == "setToken" && (string)i.Arguments[0]! == token);
        (await announced!).User.Identity!.Name.Should().Be("Ana");
    }

    [Fact]
    public async Task A_failed_login_returns_the_server_text_and_stores_nothing()
    {
        await using var context = new ClientTestContext();
        context.Account.LoginAsync("ana.admin@dib4t.local", "wrong-pass").Returns(ApiResult.Failure<string>(HttpStatusCode.BadRequest, "Password incorreta!"));

        var result = await context.Session.LoginAsync("ana.admin@dib4t.local", "wrong-pass");

        result.ErrorText.Should().Be("Password incorreta!");
        result.IsUnexpected.Should().BeFalse();
        context.Invocations("setToken").Should().Be(0);
    }

    [Fact]
    public async Task A_logout_in_another_tab_logs_this_tab_out()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana", "canSeeObras"));
        await context.Session.GetAuthenticationStateAsync();
        Task<AuthenticationState>? announced = null;
        context.Session.AuthenticationStateChanged += task => announced = task;

        await context.Tokens.OnTokenChanged(null); // what auth.js calls on the "storage" event

        (await announced!).User.Identity!.IsAuthenticated.Should().BeFalse();
        (await context.Session.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_removes_the_stored_token()
    {
        await using var context = new ClientTestContext(ClientTestContext.Token("Ana"));
        await context.Session.GetAuthenticationStateAsync();

        await context.Session.LogoutAsync();

        context.Invocations("removeToken").Should().Be(1);
        context.Tokens.Current.Should().BeNull();
    }
}
