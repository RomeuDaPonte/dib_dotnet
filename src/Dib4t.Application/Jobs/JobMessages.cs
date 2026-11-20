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

    /// <summary><c>POST print/:budgetId</c> outside Production (legacy: <c>NODE_ENV</c> not <c>"prod"</c>), before anything else (400).</summary>
    public const string PrintOnlyInProduction = "Só funciona em produção!";

    /// <summary><c>POST print/:budgetId</c>: both templates <c>false</c> (400).</summary>
    public const string ChooseTemplate = "Escolhe um template";

    /// <summary><c>POST print/:budgetId</c>: the budget has no job (400; the legacy text, verbatim).</summary>
    public const string NoJobToPrint = "Obra encontrada não encontrado";

    /// <summary><c>POST print/:budgetId</c>: no printer with the <c>producao</c> template (400).</summary>
    public const string NoProductionPrinter = "Configurações de impressora para produção não encontradas";

    /// <summary><c>POST print/:budgetId</c>: no printer with the <c>maquinacao</c> template (400).</summary>
    public const string NoMachiningPrinter = "Configurações de impressora para maquinação não encontradas";

    /// <summary><c>POST print/:budgetId</c>: the labels were sent (200, text).</summary>
    public const string Printed = "ok";
}
