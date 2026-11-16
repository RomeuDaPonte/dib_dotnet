using Dib4t.Application.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.Domain.Budgets;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/task</c> (legacy <c>routes/task.js</c>, PLAN.md §6), every route T (any signed-in user, as legacy): a
/// budget's tasks, the task types, the default task, create (D8), edit and delete. Literal routes win over the id
/// routes, in any case (Express routes are case-insensitive).
/// </summary>
[Route("api/task")]
[Authorize]
public sealed class TaskController(BudgetTasksService tasks) : LegacyControllerBase
{
    [HttpPost("createDefaultTask/{budgetId}")]
    public async Task<IActionResult> CreateDefault(string budgetId, CancellationToken cancellationToken)
    {
        var (_, error) = await ReadJsonBodyAsync(); // read like express.json(): a malformed body is still its 400
        return error ?? Answer(await tasks.CreateDefaultAsync(budgetId, cancellationToken), ToResponse);
    }

    [HttpGet("getBudgetTasks/{budgetId}")]
    public async Task<IActionResult> List(string budgetId, CancellationToken cancellationToken) =>
        Answer(await tasks.ListAsync(budgetId, cancellationToken), list => list.Select(ToResponse));

    [HttpGet("getTaskTypes")]
    public IActionResult Types() => Ok(BudgetTasksService.TaskTypeList);

    [HttpPost("")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await tasks.CreateAsync(body, cancellationToken), ToResponse);
    }

    [HttpPut("{taskId}/{budgetId}")]
    public async Task<IActionResult> Update(string taskId, string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await tasks.UpdateAsync(taskId, budgetId, body, cancellationToken), ToResponse);
    }

    [HttpDelete("{taskId}/{budgetId}")]
    public async Task<IActionResult> Delete(string taskId, string budgetId, CancellationToken cancellationToken)
    {
        var (_, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await tasks.DeleteAsync(taskId, budgetId, cancellationToken), ToResponse);
    }

    private static BudgetTaskResponse ToResponse(BudgetTask task) =>
        new(task.Id, task.BudgetId, task.TaskType, task.Description, task.UnitPrice, task.Quantity, task.Version);
}
