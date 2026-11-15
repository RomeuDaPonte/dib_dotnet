using Dib4t.Application.Account;
using Dib4t.Contracts.Account;
using Dib4t.Domain.Users;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Dib4t.SharedKernel.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/account</c> (legacy <c>routes/account.js</c>, PLAN.md §6): login (A), <c>me</c> (T, new-app only) and the
/// user management routes (S: <c>canSeeDefinicoes</c>, checked before the body, like legacy <c>authCanSeeSettings</c>).
/// </summary>
[Route("api/account")]
public sealed class AccountController(AccountService account, UserAdministrationService administration) : LegacyControllerBase
{
    /// <summary>The raw JWT as text, or a legacy 400 text (validation, unknown e-mail, wrong password).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        if (error is not null)
        {
            return error;
        }

        var result = await account.LoginAsync(body, cancellationToken);
        return result.IsSuccess
            ? LegacyResults.Text(StatusCodes.Status200OK, result.Value)
            : result.Error.ToLegacyResult();
    }

    /// <summary>The signed-in user as stored now: the client refreshes the name and roles of its menu from this.</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await account.GetUserAsync(User.GetUserId()!, cancellationToken);
        if (user is null)
        {
            // Deleted between the token check and this read: the same answer as for any token of a deleted user.
            return LegacyResults.Text(StatusCodes.Status400BadRequest, AuthTexts.InvalidToken);
        }

        return Ok(new CurrentUserResponse(user.Id, user.Name, user.Email, user.Roles, user.FieldOfCompetencies));
    }

    [HttpPost("create")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await administration.CreateAsync(body, cancellationToken), Pick);
    }

    [HttpGet("roles")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public IActionResult Roles() => Ok(UserAdministrationService.Roles);

    [HttpGet("users")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Users(CancellationToken cancellationToken)
    {
        var users = await administration.ListAsync(cancellationToken);
        return Ok(users.Select(user => new UserListItemResponse(user.Roles, user.FieldOfCompetencies, user.Id, user.Name, user.Email)));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Answer(await administration.GetAsync(id, cancellationToken), Pick);

    [HttpPut("{id}")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Update(string id, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(
            await administration.UpdateAsync(id, body, cancellationToken),
            user => new UserResponse(user.Id, user.Name, user.Email, user.Roles));
    }

    /// <summary>Not in the UI (D2). Deletes the user of <paramref name="id"/>, not the first user like legacy.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.CanSeeDefinicoes)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken) =>
        Answer(await administration.DeleteAsync(id, cancellationToken), Pick);

    private static UserResponse Pick(User user) => new(user.Id, user.Name, user.Email, user.Roles);
}
