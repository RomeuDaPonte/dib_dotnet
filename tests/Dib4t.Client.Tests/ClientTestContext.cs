using Bunit;
using Dib4t.Client.Api;
using Dib4t.Client.Auth;
using Dib4t.Client.Theme;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using NSubstitute;

namespace Dib4t.Client.Tests;

/// <summary>
/// bUnit context with the client's auth services as Program.cs registers them, a substituted <see cref="IAccountApi"/>
/// and the localStorage module (<c>js/auth.js</c>) faked by bUnit's JS interop.
/// </summary>
public class ClientTestContext : BunitContext
{
    public const string Module = "./js/auth.js";

    public ClientTestContext(string? storedToken = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // MudBlazor's own JS calls
        StorageModule = JSInterop.SetupModule(Module);
        StorageModule.Setup<string?>("getToken").SetResult(storedToken);
        StorageModule.SetupVoid("setToken", _ => true).SetVoidResult();
        StorageModule.SetupVoid("removeToken").SetVoidResult();
        StorageModule.SetupModule("watchToken", _ => true);

        Account.GetMeAsync().Returns(ApiResult.Failure<SessionUser>(null, AccountApi.UnexpectedError));
        Services.AddMudServices();
        Services.AddAuthorizationCore();
        // bUnit registers throwing placeholders first; use the real services, as the app does.
        Services.Replace(ServiceDescriptor.Transient<IAuthorizationService, DefaultAuthorizationService>());
        Services.Replace(ServiceDescriptor.Transient<IAuthorizationPolicyProvider, DefaultAuthorizationPolicyProvider>());
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton(Account);
        Services.AddScoped<TokenStore>();
        Services.AddScoped<AuthSession>();
        Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<AuthSession>());
        Services.AddScoped<ThemeState>();
    }

    public ThemeState Theme => Services.GetRequiredService<ThemeState>();

    public IAccountApi Account { get; } = Substitute.For<IAccountApi>();

    public BunitJSModuleInterop StorageModule { get; }

    public TokenStore Tokens => Services.GetRequiredService<TokenStore>();

    public AuthSession Session => Services.GetRequiredService<AuthSession>();

    /// <summary>A readable (unsigned) token with the legacy claims, as the client decodes it.</summary>
    public static string Token(string name = "Ana Administradora", params string?[] roles)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_id"] = "5f1e9a2b3c4d5e6f70819203",
            ["name"] = name,
            ["email"] = "ana.admin@dib4t.local",
            ["roles"] = roles,
            ["fieldOfCompetencies"] = "Desenvolvimento",
            ["iat"] = 1794650400,
        });
        return "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9."
            + System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload)) + ".signature";
    }

    public int Invocations(string identifier) => StorageModule.Invocations.Count(i => i.Identifier == identifier);
}
