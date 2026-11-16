using Dib4t.Domain.Budgets;
using Dib4t.Domain.Entities;

namespace Dib4t.Application.Budgets;

/// <summary>The <c>budgets</c> collection (legacy <c>repositories/budgetRepository.js</c> and the budget routes' saves).</summary>
public interface IBudgetRepository
{
    /// <summary>
    /// Legacy <c>getBudgetsWithLastRevision</c> before its dedupe: <c>find().sort({ budgetNumber: -1, revision: -1 })
    /// .limit(200)</c> with the client's and the author's names.
    /// </summary>
    Task<IReadOnlyList<BudgetRow>> ListNewestAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>searchBudgets</c> before its dedupe: the budgets whose number, client name, descriptive, person in
    /// charge or author name matches <paramref name="pattern"/> (a regular expression, case-insensitive, evaluated by
    /// MongoDB; budgets whose client or author no longer exists are left out, as legacy's <c>$unwind</c> does),
    /// sorted like <see cref="ListNewestAsync"/>. <c>null</c> when MongoDB refuses the pattern, with its reason.
    /// </summary>
    Task<(IReadOnlyList<BudgetRow>? Rows, string? Refusal)> SearchAsync(string pattern, CancellationToken cancellationToken);

    /// <summary>The budget with this <c>_id</c> (24 lowercase hex digits), or <c>null</c>.</summary>
    Task<Budget?> FindByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>Legacy <c>getAllRevisions</c>: every document with this number, highest revision first.</summary>
    Task<IReadOnlyList<Budget>> ListRevisionsAsync(string budgetNumber, CancellationToken cancellationToken);

    /// <summary>Legacy <c>getRevisionsCount</c>: the number of documents with this number.</summary>
    Task<int> CountRevisionsAsync(string budgetNumber, CancellationToken cancellationToken);

    /// <summary>Legacy <c>getBudgetCountForThisYear</c>: distinct budget numbers with a document dated from <paramref name="from"/> on.</summary>
    Task<int> CountNumbersSinceAsync(DateTime from, CancellationToken cancellationToken);

    /// <summary>Whether any document has this number (the numbering's collision guard, PLAN.md §4.7).</summary>
    Task<bool> NumberExistsAsync(string budgetNumber, CancellationToken cancellationToken);

    /// <summary>
    /// The user behind <c>madeBy</c> as a populated budget shows it (legacy <c>populate("madeBy")</c>), or <c>null</c>.
    /// </summary>
    Task<BudgetAuthor?> FindAuthorAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a budget in the legacy shape (Mongoose defaults first, then the route's fields in its order, a
    /// missing value left out, <c>__v: 0</c>; PLAN.md §4.3 rules 1, 6). Returns the stored budget.
    /// </summary>
    Task<Budget> InsertAsync(NewBudget budget, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>budget.update(body, madeBy)</c> + <c>save()</c>: <c>madeBy</c>, <c>descriptive</c>,
    /// <c>personInCharge</c>, <c>complete = false</c>, <c>wasPdfDownloaded = false</c>, each written only when it
    /// differs from the stored value (a missing flag counts as its default, <c>false</c>); nothing when nothing
    /// differs. The budget as saved, or <c>null</c> when it does not exist (any more).
    /// </summary>
    Task<Budget?> UpdateAsync(string id, BudgetEdit edit, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>budget.patch(propertyName, value)</c> + <c>save()</c>: <c>complete = false</c>,
    /// <c>wasPdfDownloaded = false</c>, then the property, each written only when the final value differs from the
    /// stored one (a missing field counts as its schema default). The budget as saved, or <c>null</c> when it does
    /// not exist (any more).
    /// </summary>
    Task<Budget?> PatchAsync(string id, BudgetPatch patch, CancellationToken cancellationToken);

    /// <summary>Legacy <c>budget.setIncomplete()</c> + <c>save()</c> (the task routes): both flags <c>false</c>, written only when they change.</summary>
    Task SetIncompleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// The save of <c>PUT /import/:id</c> on the receiving budget (D24): <c>complete</c> and <c>wasPdfDownloaded</c>
    /// reset, then <c>total</c> and <c>description</c> when given; nothing is removed. Written only where it differs.
    /// The budget as saved, or <c>null</c>.
    /// </summary>
    Task<Budget?> ImportAsync(string id, BudgetImport import, CancellationToken cancellationToken);
}

/// <summary>The <c>tasks</c> of a budget (legacy <c>repositories/taskRepository.js</c>).</summary>
public interface IBudgetTaskRepository
{
    /// <summary>Legacy <c>findByBudgetId</c>: <c>find({ budgetId })</c> in natural order.</summary>
    Task<IReadOnlyList<BudgetTask>> ListByBudgetAsync(string budgetId, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>createDefaultTask</c> + <c>save</c>: <c>{ _id, budgetId, taskType: "automation", description: "",
    /// unitPrice, quantity: 1, __v: 0 }</c>.
    /// </summary>
    Task<BudgetTask> InsertDefaultAsync(string budgetId, double unitPrice, CancellationToken cancellationToken);

    /// <summary>
    /// The copies of clone and new revision (legacy <c>new Task({ taskType, budgetId, description, unitPrice,
    /// quantity })</c> + <c>insertMany</c>), in this order, a missing description left out.
    /// </summary>
    Task InsertCopiesAsync(string budgetId, IReadOnlyList<BudgetTask> tasks, CancellationToken cancellationToken);

    /// <summary>The task with this <c>_id</c> (24 lowercase hex digits), or <c>null</c>.</summary>
    Task<BudgetTask?> FindByIdAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy <c>task.update(body)</c> + <c>save()</c>: description (missing = removed), unit price, quantity and type,
    /// each written only when it differs from the stored value (numbers as JavaScript compares them, so the stored
    /// type is kept). The task as saved, or <c>null</c> when it does not exist (any more).
    /// </summary>
    Task<BudgetTask?> UpdateAsync(string taskId, TaskInput task, CancellationToken cancellationToken);

    /// <summary>D8: <c>new Task({ budgetId, taskType, description, unitPrice, quantity })</c> + <c>save()</c> (the default task's field order).</summary>
    Task<BudgetTask> InsertAsync(string budgetId, TaskInput task, CancellationToken cancellationToken);

    /// <summary>Legacy <c>deleteTask</c> (<c>findByIdAndDelete</c>): the deleted task, or <c>null</c> when there was none.</summary>
    Task<BudgetTask?> DeleteAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>Legacy <c>deleteByBudgetId</c> (<c>deleteMany({ budgetId })</c>): the number of deleted tasks.</summary>
    Task<long> DeleteByBudgetAsync(string budgetId, CancellationToken cancellationToken);
}

/// <summary>A list or search row: a budget with its client's and author's names (<c>null</c> when not found).</summary>
public sealed record BudgetRow(Budget Budget, string? ClientName, string? MadeByName);

/// <summary>A budget with its populated client and author (<c>null</c> when the referenced document is missing).</summary>
public sealed record BudgetDetails(Budget Budget, BusinessEntity? Client, BudgetAuthor? MadeBy);

/// <summary>
/// A user as a populated budget shows it. The password hash is never read for this (the legacy API sends it; the
/// new app does not, PLAN.md §6 known differences).
/// </summary>
/// <param name="Id">The user's <c>_id</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Email">Login e-mail.</param>
/// <param name="Roles">The stored roles (<c>null</c> elements kept); <c>[]</c> when missing (Mongoose default).</param>
/// <param name="FieldOfCompetencies">Stored value, or the Mongoose default <c>Desenvolvimento</c>.</param>
/// <param name="Version">The stored <c>__v</c>.</param>
public sealed record BudgetAuthor(string Id, string? Name, string? Email, IReadOnlyList<string?> Roles, string FieldOfCompetencies, int Version);

/// <summary>A budget to insert. <see cref="Revision"/> 0 for create and clone.</summary>
public sealed record NewBudget(
    double Revision,
    string ClientId,
    string? Descriptive,
    string? PersonInCharge,
    string MadeById,
    string BudgetNumber,
    string PaymentMethod,
    double Margin,
    double? Total,
    double DaysNeededToDoTheWork,
    double Validity,
    string? Description,
    DateTime Date);

/// <summary>What <c>PUT /import/:id</c> writes on the receiving budget (D24); <c>null</c> = left as it is.</summary>
/// <param name="Total">The total recomputed from all its tasks (tasks were added).</param>
/// <param name="Description">The open budget's description (ticked and present).</param>
public sealed record BudgetImport(double? Total, string? Description);

/// <summary>The values of an edit (legacy <c>budget.update</c>).</summary>
public sealed record BudgetEdit(string MadeById, string Descriptive, string PersonInCharge);
