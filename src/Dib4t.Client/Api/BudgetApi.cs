using Dib4t.Contracts.Budgets;
using Dib4t.Contracts.Entities;

namespace Dib4t.Client.Api;

/// <summary>The budget list routes of <c>/api/budget</c> and the client list of the budget dialogs (PLAN.md §6, §8.2).</summary>
public interface IBudgetApi
{
    /// <summary><c>GET /api/budget</c>: the latest revision of each number.</summary>
    Task<ApiResult<IReadOnlyList<BudgetListItemResponse>>> ListAsync();

    /// <summary><c>GET /api/budget/search?q=</c> (the text is a regular expression, as legacy).</summary>
    Task<ApiResult<IReadOnlyList<BudgetListItemResponse>>> SearchAsync(string text);

    Task<ApiResult<BudgetResponse>> GetAsync(string id);

    Task<ApiResult<IReadOnlyList<BudgetRevisionResponse>>> ListRevisionsAsync(string budgetNumber);

    /// <summary><c>POST /api/budget</c>: the new budget, or (200 text) the notice that nothing was created.</summary>
    Task<ApiResult<BudgetSaveResult>> CreateAsync(NewBudgetRequest budget);

    Task<ApiResult<BudgetSaveResult>> CloneAsync(string id, NewBudgetRequest budget);

    Task<ApiResult<BudgetResponse>> CreateRevisionAsync(string id);

    Task<ApiResult<BudgetResponse>> UpdateAsync(string id, UpdateBudgetRequest budget);

    /// <summary><c>GET /api/entity/getallclients</c>: the entities a budget can be made for, sorted by name.</summary>
    Task<ApiResult<IReadOnlyList<EntityResponse>>> ListClientsAsync();

    /// <summary><c>PUT /api/budget/patchProperty/:id</c>: one property (the server resets the complete/PDF flags).</summary>
    Task<ApiResult<BudgetResponse>> PatchAsync(string id, PatchBudgetRequest patch);

    /// <summary><c>PUT /api/budget/import/:id</c>: tasks and/or description from another budget number.</summary>
    Task<ApiResult<BudgetResponse>> ImportAsync(string id, ImportBudgetRequest import);
}

/// <summary>A create or clone answer: the budget, or the legacy 200 notice (the chosen entity is a supplier).</summary>
public sealed record BudgetSaveResult(BudgetResponse? Budget, string? Notice);

/// <inheritdoc cref="IBudgetApi"/>
public sealed class BudgetApi(HttpClient http) : IBudgetApi
{
    private const string Root = "api/budget";

    public Task<ApiResult<IReadOnlyList<BudgetListItemResponse>>> ListAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<BudgetListItemResponse>>(http, HttpMethod.Get, Root + "/");

    public Task<ApiResult<IReadOnlyList<BudgetListItemResponse>>> SearchAsync(string text) =>
        ApiCalls.SendAsync<IReadOnlyList<BudgetListItemResponse>>(http, HttpMethod.Get, Root + "/search?q=" + Uri.EscapeDataString(text));

    public Task<ApiResult<BudgetResponse>> GetAsync(string id) =>
        ApiCalls.SendAsync<BudgetResponse>(http, HttpMethod.Get, Root + "/getbyid/" + Uri.EscapeDataString(id));

    public Task<ApiResult<IReadOnlyList<BudgetRevisionResponse>>> ListRevisionsAsync(string budgetNumber) =>
        ApiCalls.SendAsync<IReadOnlyList<BudgetRevisionResponse>>(http, HttpMethod.Get, Root + "/revisions/" + Uri.EscapeDataString(budgetNumber));

    public Task<ApiResult<BudgetSaveResult>> CreateAsync(NewBudgetRequest budget) => SaveAsync(Root, budget);

    public Task<ApiResult<BudgetSaveResult>> CloneAsync(string id, NewBudgetRequest budget) => SaveAsync(Root + "/clone/" + Uri.EscapeDataString(id), budget);

    public Task<ApiResult<BudgetResponse>> CreateRevisionAsync(string id) =>
        ApiCalls.SendAsync<BudgetResponse>(http, HttpMethod.Post, Root + "/newrevision/" + Uri.EscapeDataString(id), new { });

    public Task<ApiResult<BudgetResponse>> UpdateAsync(string id, UpdateBudgetRequest budget) =>
        ApiCalls.SendAsync<BudgetResponse>(http, HttpMethod.Put, Root + "/" + Uri.EscapeDataString(id), budget);

    public Task<ApiResult<IReadOnlyList<EntityResponse>>> ListClientsAsync() =>
        ApiCalls.SendAsync<IReadOnlyList<EntityResponse>>(http, HttpMethod.Get, "api/entity/getallclients");

    public Task<ApiResult<BudgetResponse>> PatchAsync(string id, PatchBudgetRequest patch) =>
        ApiCalls.SendAsync<BudgetResponse>(http, HttpMethod.Put, Root + "/patchProperty/" + Uri.EscapeDataString(id), patch);

    public Task<ApiResult<BudgetResponse>> ImportAsync(string id, ImportBudgetRequest import) =>
        ApiCalls.SendAsync<BudgetResponse>(http, HttpMethod.Put, Root + "/import/" + Uri.EscapeDataString(id), import);

    /// <summary>A 200 answer is JSON (the budget) or plain text (the supplier notice).</summary>
    private async Task<ApiResult<BudgetSaveResult>> SaveAsync(string path, NewBudgetRequest budget)
    {
        var text = await ApiCalls.SendAsync<string>(http, HttpMethod.Post, path, budget, raw: true);
        if (!text.IsSuccess)
        {
            return ApiResult.Failure<BudgetSaveResult>(text.Status, text.ErrorText!);
        }

        return text.Value!.StartsWith('{')
            ? ApiResult.Success(new BudgetSaveResult(System.Text.Json.JsonSerializer.Deserialize<BudgetResponse>(text.Value, ApiCalls.Json), null))
            : ApiResult.Success(new BudgetSaveResult(null, text.Value));
    }
}
