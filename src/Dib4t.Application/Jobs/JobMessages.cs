namespace Dib4t.Application.Jobs;

/// <summary>The legacy texts of <c>routes/job.js</c>, verbatim.</summary>
public static class JobMessages
{
    /// <summary><c>POST /</c>: no budget with <c>budgetId</c> (400).</summary>
    public const string BudgetMissing = "Orçamento não encontrado";

    /// <summary><c>POST /</c>: a revision of the budget's number already has a job (400).</summary>
    public const string AlreadyCreated = "Obra baseada neste orçamento já foi criada anteriormente";

    /// <summary><c>POST /</c>: the budget's number has no revision 0 (400).</summary>
    public const string RevisionZeroMissing = "Algo deu errado!";

    /// <summary><c>PUT statistics/:jobId</c>: no such job (400).</summary>
    public const string NotFoundForStatistics = "Obra não encontrada";

    /// <summary>The other job routes: no such job (400).</summary>
    public const string NotFound = "Obra não existe";

    /// <summary><c>PUT statistics/:jobId</c>: budgeted − used ≠ the sent deviation (400).</summary>
    public const string WrongDeviation = "Erro no valor do desvio";

    /// <summary><c>PUT statistics/:jobId</c> to "Faturada" below 100 % billed: the 405 <c>reason</c>.</summary>
    public const string InvoicedBelow100 = "Valor faturado inferior a 100%";

    /// <summary><c>GET getByBudgetId/:budgetId</c>: no job made from that budget (400).</summary>
    public const string NoJobForBudget = "Não há obra corresponde a esse orçamento";
}
