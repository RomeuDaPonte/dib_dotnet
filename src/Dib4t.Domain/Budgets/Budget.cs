namespace Dib4t.Domain.Budgets;

/// <summary>
/// One revision of a budget (legacy <c>models/budget.js</c>, collection <c>budgets</c>, PLAN.md §4.2). Values are as
/// stored, with the Mongoose defaults for fields an older document lacks (§4.4): revision 0, not complete, PDF not
/// downloaded, margin 30, 60 days, validity 30, no job. Texts are never trimmed. Every revision of a budget shares
/// its <see cref="BudgetNumber"/>.
/// </summary>
/// <param name="Id">The <c>_id</c> as 24 lowercase hex digits.</param>
/// <param name="Revision">0 for the first version; a new revision gets the number of documents with this number.</param>
/// <param name="Complete">Set by the user ("Concluir"); any change sets it back to <c>false</c>.</param>
/// <param name="WasPdfDownloaded">Set when the PDF is downloaded; any change sets it back to <c>false</c>.</param>
/// <param name="Margin">Margin in percent.</param>
/// <param name="DaysNeededToDoTheWork">Delivery time in days (PDF; job due date).</param>
/// <param name="Validity">Validity of the proposal in days.</param>
/// <param name="HasGenerateJob">A job was created from this revision: the budget can no longer change.</param>
/// <param name="ClientId">→ <c>entities._id</c>.</param>
/// <param name="Descriptive">Short title, part of the NAS folder name.</param>
/// <param name="PersonInCharge">Contact person at the client ("Responsável"); missing in some old documents.</param>
/// <param name="MadeById">→ <c>users._id</c> ("Elaborado por").</param>
/// <param name="BudgetNumber"><c>YY-NNNN</c>.</param>
/// <param name="PaymentMethod">Payment terms (text of a payment method).</param>
/// <param name="Total">Total with margin (€); missing in some old documents.</param>
/// <param name="Description">The PDF text; missing in old budgets.</param>
/// <param name="Date">Creation time (UTC).</param>
/// <param name="Version">Mongoose <c>__v</c>.</param>
public sealed record Budget(
    string Id,
    double Revision,
    bool Complete,
    bool WasPdfDownloaded,
    double Margin,
    double DaysNeededToDoTheWork,
    double Validity,
    bool HasGenerateJob,
    string? ClientId,
    string? Descriptive,
    string? PersonInCharge,
    string? MadeById,
    string? BudgetNumber,
    string? PaymentMethod,
    double? Total,
    string? Description,
    DateTime? Date,
    int Version)
{
    /// <summary>
    /// The row menu's rules (legacy <c>budgetOptions.jsx</c>, PLAN.md §7.1): a new revision only from a complete
    /// budget without a job.
    /// </summary>
    public bool CanCreateRevision => Complete && !HasGenerateJob;
}
