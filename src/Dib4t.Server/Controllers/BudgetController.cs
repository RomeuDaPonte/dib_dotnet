using Dib4t.Application.Budgets;
using Dib4t.Contracts.Budgets;
using Dib4t.Domain.Budgets;
using Dib4t.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dib4t.Server.Controllers;

/// <summary>
/// <c>/api/budget</c> (legacy <c>routes/budget.js</c>, PLAN.md §6), every route T (any signed-in user; legacy checks
/// no role here): the list, search, one budget, its revisions, create, clone, new revision, edit, and the details
/// routes <c>patchProperty</c> and <c>import</c>, and <c>getPdf</c>.
/// </summary>
[Route("api/budget")]
[Authorize]
public sealed class BudgetController(BudgetsService budgets, BudgetPdfService pdfs) : LegacyControllerBase
{
    /// <summary>The legacy download headers: the body is the PDF as base64 text (the legacy UI decodes it).</summary>
    private const string PdfContentDisposition = "attachment;filename=\"filename.pdf\"";
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok((await budgets.ListAsync(cancellationToken)).Select(ToResponse));

    [HttpGet("search")]
    public async Task<IActionResult> Search(CancellationToken cancellationToken) =>
        Answer(await budgets.SearchAsync(LegacyQuery.ToJson(Request.Query), cancellationToken), rows => rows.Select(ToResponse));

    [HttpGet("getById/{budgetId}")]
    public async Task<IActionResult> Get(string budgetId, CancellationToken cancellationToken) =>
        Answer(await budgets.GetAsync(budgetId, cancellationToken), ToResponse);

    [HttpGet("revisions/{budgetNumber}")]
    public async Task<IActionResult> Revisions(string budgetNumber, CancellationToken cancellationToken) =>
        Ok((await budgets.ListRevisionsAsync(budgetNumber, cancellationToken)).Select(r => ToRevisionResponse(r.Budget, r.MadeBy)));

    [HttpPost("")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Saved(await budgets.CreateAsync(body, cancellationToken));
    }

    [HttpPost("clone/{budgetId}")]
    public async Task<IActionResult> Clone(string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Saved(await budgets.CloneAsync(budgetId, body, cancellationToken));
    }

    [HttpPost("newrevision/{budgetId}")]
    public async Task<IActionResult> NewRevision(string budgetId, CancellationToken cancellationToken)
    {
        var (_, error) = await ReadJsonBodyAsync(); // read like express.json(): a malformed body is still its 400
        return error ?? Answer(await budgets.CreateRevisionAsync(budgetId, cancellationToken), ToResponse);
    }

    [HttpPut("{budgetId}")]
    public async Task<IActionResult> Update(string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await budgets.UpdateAsync(budgetId, body, cancellationToken), ToResponse);
    }

    [HttpPut("patchProperty/{budgetId}")]
    public async Task<IActionResult> Patch(string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await budgets.PatchAsync(budgetId, body, cancellationToken), ToResponse);
    }

    [HttpPut("import/{budgetId}")]
    public async Task<IActionResult> Import(string budgetId, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadJsonBodyAsync();
        return error ?? Answer(await budgets.ImportAsync(budgetId, body, cancellationToken), ToResponse);
    }

    /// <summary>
    /// <c>GET /getPdf/:id</c>: the PDF as a base64 string with <c>Content-Type: application/pdf</c> (legacy contract,
    /// <c>pdfDoc.getBase64</c>), and <c>wasPdfDownloaded</c> set.
    /// </summary>
    [HttpGet("getPdf/{budgetId}")]
    public async Task<IActionResult> GetPdf(string budgetId, CancellationToken cancellationToken)
    {
        var result = await pdfs.GetAsync(budgetId, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToLegacyResult();
        }

        Response.Headers.ContentDisposition = PdfContentDisposition;
        return new ContentResult { StatusCode = StatusCodes.Status200OK, ContentType = "application/pdf", Content = Convert.ToBase64String(result.Value.Pdf) };
    }

    /// <summary>The new budget, or the legacy 200 text when nothing was created (a supplier).</summary>
    private IActionResult Saved(Application.Common.Result<BudgetSaved> result) =>
        result.IsSuccess && result.Value.Notice is { } notice
            ? LegacyResults.Text(StatusCodes.Status200OK, notice)
            : Answer(result, saved => ToResponse(saved.Budget!));

    private static BudgetListItemResponse ToResponse(BudgetRow row)
    {
        var b = row.Budget;
        return new BudgetListItemResponse(b.Id, b.BudgetNumber, row.ClientName, b.Descriptive, b.PersonInCharge, row.MadeByName, b.Date,
            b.Revision, b.Complete, b.WasPdfDownloaded, b.HasGenerateJob, b.Total, b.DaysNeededToDoTheWork);
    }

    private static BudgetResponse ToResponse(BudgetDetails details) => BudgetResponses.ToResponse(details);

    private static BudgetRevisionResponse ToRevisionResponse(Budget b, BudgetAuthor? author) =>
        new(b.Revision, b.Complete, b.WasPdfDownloaded, b.Margin, b.DaysNeededToDoTheWork, b.Validity, b.HasGenerateJob, b.Id, b.ClientId,
            b.Descriptive, b.PersonInCharge, author is null ? null : ToResponse(author), b.BudgetNumber, b.PaymentMethod, b.Total,
            b.Description, b.Date, b.Version);

    private static BudgetAuthorResponse ToResponse(BudgetAuthor author) => BudgetResponses.ToResponse(author);
}
