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

    /// <summary><c>GET /getById/:id</c>: no budget with this id (400).</summary>
    public const string NotFound = "Orçamento não existe na base de dados";

    /// <summary>D21 (ADR-0005): a new or changed descriptive must not contain <c>/</c> (400).</summary>
    public const string DescriptiveWithSlash = "O descritivo não pode conter o carácter \"/\".";

    /// <summary><c>GET /search</c>: prefix of the answer when the pattern cannot be used (D9: 400 instead of 500).</summary>
    public const string SearchFailedPrefix = "Error performing search: ";
}
