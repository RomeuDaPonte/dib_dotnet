using Dib4t.Application.Account;
using Dib4t.Infrastructure.Auth;
using Dib4t.SharedKernel.Auth;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dib4t.Server.Auth;

/// <summary>
/// Authentication and authorization (PLAN.md §4.6): the standard JwtBearer handler reads the token from
/// <c>x-auth-token</c>, checks the HS256 signature with the app's own key, and accepts tokens without expiry. The
/// user is then loaded from the database by the token's <c>_id</c> (cached briefly), and the request runs with that
/// user's current roles; a token whose user is gone counts as invalid.
/// </summary>
public static partial class AuthSetup
{
    public static IServiceCollection AddDib4tAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UserSessionOptions>(configuration.GetSection(UserSessionOptions.SectionName));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.Value.KeyBytes()),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    // Tokens never expire (decided, §4.6): only the logout button ends a session.
                    ValidateLifetime = false,
                    RequireExpirationTime = false,
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ReadTokenHeader,
                    OnTokenValidated = LoadUserFromDatabaseAsync,
                };
            });

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.CanSeeDefinicoes, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireRole(RoleNames.CanSeeDefinicoes));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, LegacyAuthorizationResultHandler>();
        return services;
    }

    /// <summary>
    /// Legacy <c>req.header("x-auth-token")</c>: the raw token, no <c>Bearer</c> scheme. Read only for endpoints that
    /// require authorization: ASP.NET Core adds the authentication middleware on its own and makes the only scheme the
    /// default, so without this check pages, static files and anonymous routes (login) would load the token's user
    /// from the database whenever a request carries the header.
    /// </summary>
    private static Task ReadTokenHeader(MessageReceivedContext context)
    {
        var metadata = context.HttpContext.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<IAuthorizeData>() is null || metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            context.NoResult();
            return Task.CompletedTask;
        }

        var token = context.Request.Headers[AuthTexts.TokenHeader].ToString();
        context.Token = token.Length == 0 ? null : token;
        return Task.CompletedTask;
    }

    private static async Task LoadUserFromDatabaseAsync(TokenValidatedContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthSetup));
        if (context.SecurityToken is not JsonWebToken token || !token.TryGetPayloadValue<string>(Dib4tClaims.UserId, out var userId))
        {
            LogTokenWithoutUserId(logger);
            context.Fail("The token has no _id.");
            return;
        }

        var account = context.HttpContext.RequestServices.GetRequiredService<AccountService>();
        var user = await account.GetSessionUserAsync(userId, context.HttpContext.RequestAborted);
        if (user is null)
        {
            LogUnknownUser(logger, userId);
            context.Fail("The token's user does not exist.");
            return;
        }

        context.Principal = Dib4tClaims.ToPrincipal(user);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Token rejected: it has no _id")]
    private static partial void LogTokenWithoutUserId(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Token rejected: user {UserId} does not exist")]
    private static partial void LogUnknownUser(ILogger logger, string userId);
}
