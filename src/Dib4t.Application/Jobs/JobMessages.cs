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
}
