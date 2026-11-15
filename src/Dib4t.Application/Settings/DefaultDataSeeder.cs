using Dib4t.Application.Account;
using Dib4t.Domain.Settings;
using Dib4t.SharedKernel.Vocabulary;
using Microsoft.Extensions.Logging;

namespace Dib4t.Application.Settings;

/// <summary>
/// Legacy <c>startup/seedBd.js</c>, run once at startup (PLAN.md §7.2): the default prices when there is no prices
/// document, then the administrator <see cref="AdminEmail"/> when no user has that e-mail, with the legacy hash and
/// roles. Idempotent: an existing document is never changed.
/// </summary>
public sealed partial class DefaultDataSeeder(IPricesRepository prices, IUserRepository users, ILogger<DefaultDataSeeder> logger)
{
    public const string AdminEmail = "userlogado@gmail.com";
    public const string AdminName = "User Logado";

    /// <summary>The legacy seed's bcrypt hash (<c>models/user.js</c> <c>createAdminUser</c>), kept so the same password works.</summary>
    public const string AdminPasswordHash = "$2b$10$fSMIGpdKqJ8Eqmkf4NAWEu0hGnkNRaexlLWKeI0kSuMFdnrryjUKy";

    /// <summary>The legacy seed's roles, in its order (no <c>canSeeLeituras</c>).</summary>
    public static IReadOnlyList<string> AdminRoles { get; } =
    [
        RoleNames.CanSeeDefinicoes, RoleNames.CanSeeEntidades, RoleNames.CanSeeOrcamentos, RoleNames.CanSeeObras,
        RoleNames.CanSeeHistorico, RoleNames.CanSeeDashboard,
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await prices.FindAsync(cancellationToken) is null)
        {
            await prices.InsertAsync(Prices.Defaults, cancellationToken);
            LogPricesSeeded();
        }
        else
        {
            LogPricesPresent();
        }

        if (await users.FindByEmailAsync(AdminEmail, cancellationToken) is null)
        {
            await users.InsertAsync(new NewUser(AdminName, AdminEmail, AdminPasswordHash, [.. AdminRoles], FieldsOfCompetence.Default), cancellationToken);
            LogAdminSeeded(AdminEmail);
        }
        else
        {
            LogAdminPresent(AdminEmail);
        }
    }

    [LoggerMessage(EventId = 60, Level = LogLevel.Information, Message = "Default prices created")]
    private partial void LogPricesSeeded();

    [LoggerMessage(EventId = 61, Level = LogLevel.Information, Message = "Prices already defined")]
    private partial void LogPricesPresent();

    [LoggerMessage(EventId = 62, Level = LogLevel.Information, Message = "Administrator {Email} created")]
    private partial void LogAdminSeeded(string email);

    [LoggerMessage(EventId = 63, Level = LogLevel.Information, Message = "Administrator {Email} already exists")]
    private partial void LogAdminPresent(string email);
}
