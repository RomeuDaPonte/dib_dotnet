using Dib4t.Application.Account;
using Dib4t.Contracts.Account;
using Dib4t.Server.Auth;
using Dib4t.Server.Http;
using Dib4t.SharedKernel.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/account</c> (legacy <c>routes/account.js</c>, PLAN.md §6). This step: login (A) and <c>me</c> (T, new-app
/// only). User management (create, list, edit, delete, roles) comes with the users slice (Phase 2).
/// </summary>
[Route("api/account")]
public sealed class AccountController(AccountService account) : ControllerBase
{
    /// <summary>The raw JWT as text, or a legacy 400 text (validation, unknown e-mail, wrong password).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(CancellationToken cancellationToken)
    {
        var (body, error) = await LegacyJsonBody.ReadAsync(Request);
        if (error is not null)
        {
            return new LegacyResult(error);
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

    /// <summary>Runs a minimal-API <see cref="IResult"/> from a controller action.</summary>
    private sealed class LegacyResult(IResult result) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
    }
}
