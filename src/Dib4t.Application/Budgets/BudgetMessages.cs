namespace Dib4t.Application.Budgets;

/// <summary>The legacy texts of <c>routes/budget.js</c>, verbatim, plus the texts of the decided fixes (PLAN.md §9).</summary>
public static class BudgetMessages
{
    /// <summary><c>POST /</c>, <c>POST /clone/:id</c>: no entity with the client id (400).</summary>
    public const string ClientMissing = "Cliente não existe!";

    /// <summary>
    /// <c>POST /</c>, <c>POST /clone/:id</c>: the client is a supplier. Answered with status <b>200</b> and nothing is
    /// created (legacy behavior, kept).
    /// </summary>
    public const string ClientIsSupplier = "Entidade registada como fornecedor!";

    /// <summary><c>POST /</c>, <c>POST /clone/:id</c>, <c>PUT /:id</c>: no user with <c>madeById</c> (400).</summary>
    public const string AuthorMissing = "Erro interno relacionado com user!";

    /// <summary><c>POST /clone/:id</c>, <c>POST /newrevision/:id</c>, <c>PUT /:id</c>: no budget with this id (400).</summary>
    public const string Missing = "Orçamento não existe";

    /// <summary>
    /// <c>POST /</c>, <c>POST /clone/:id</c> (Production, NAS on): the budget folder is not there after the
    /// <c>mkdir</c> commands (500; the budget and its tasks stay saved, D6).
    /// </summary>
    public const string FolderNotCreated = "Erro ao criar pasta";

    /// <summary><c>GET /getById/:id</c>: no budget with this id (400).</summary>
    public const string NotFound = "Orçamento não existe na base de dados";

    /// <summary>D21 (ADR-0005): a new or changed descriptive must not contain <c>/</c> (400).</summary>
    public const string DescriptiveWithSlash = "O descritivo não pode conter o carácter \"/\".";

    /// <summary><c>GET /search</c>: prefix of the answer when the pattern cannot be used (D9: 400 instead of 500).</summary>
    public const string SearchFailedPrefix = "Error performing search: ";

    /// <summary><c>PUT /patchProperty/:id</c>: no budget with this id (400; the legacy text has a "!").</summary>
    public const string PatchMissing = "Orçamento não existe!";

    /// <summary><c>PUT /patchProperty/:id</c>: prefix of the 400 when the body is refused, followed by JavaScript <c>String(propertyName)</c>.</summary>
    public const string PatchRefusedPrefix = "Não é possível atualizar ";

    /// <summary><c>PUT /import/:id</c>: prefix of the Joi 400.</summary>
    public const string ImportRefusedPrefix = "Erro: ";

    /// <summary><c>PUT /import/:id</c>: no budget with the typed number (400; legacy text, D24: the receiving budget's number).</summary>
    public const string ImportSourceMissing = "Orçamento com esse número não existe";

    /// <summary>
    /// <c>PUT /import/:id</c> and the task routes: the budget has a job, so it can no longer change (400). The patch and
    /// edit routes answer such a budget unchanged instead.
    /// </summary>
    public const string HasJob = "Orçamento gerou obra!";

    /// <summary>D24: <c>PUT /import/:id</c> with the open budget's own number when it is the highest revision (400).</summary>
    public const string ImportIntoItself = "Não é possível importar para o próprio orçamento.";

    /// <summary>The task routes: no task with this id (400).</summary>
    public const string TaskMissing = "Tarefa não encontrada";

    /// <summary><c>DELETE /api/task/:taskId/:budgetId</c>: nothing was deleted (500; after the budget was marked incomplete).</summary>
    public const string TaskNotDeleted = "Erro no servidor";
}
