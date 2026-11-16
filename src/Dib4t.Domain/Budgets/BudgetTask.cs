namespace Dib4t.Domain.Budgets;

/// <summary>
/// A line of a budget (legacy <c>models/task.js</c>, collection <c>tasks</c>, PLAN.md §4.2): type, description,
/// unit price and quantity (Int32 or Double, fractional hours are common). Values are as stored.
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="BudgetId">→ <c>budgets._id</c>.</param>
/// <param name="TaskType">One of <c>TaskTypes</c>.</param>
/// <param name="Description">Free text; missing in some old documents (copies leave it missing too).</param>
/// <param name="UnitPrice">Price per unit (€).</param>
/// <param name="Quantity">Units (hours, pieces).</param>
/// <param name="Version">Mongoose <c>__v</c>.</param>
public sealed record BudgetTask(
    string Id,
    string? BudgetId,
    string? TaskType,
    string? Description,
    double? UnitPrice,
    double? Quantity,
    int Version);
