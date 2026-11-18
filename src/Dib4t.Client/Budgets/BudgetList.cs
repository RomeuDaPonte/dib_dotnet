using System.Globalization;
using Dib4t.Contracts.Budgets;
using Dib4t.SharedKernel;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Client.Budgets;

/// <summary>
/// The budget table's client-side rules (legacy <c>budgets/budgetsTable.jsx</c>, <c>budgetsTableRow.jsx</c>,
/// <c>budgetOptions.jsx</c>; PLAN.md §7.1): rows after a create/clone/revision, dates, the copy path and the row menu.
/// </summary>
public static class BudgetList
{
    /// <summary>
    /// Legacy <c>newBudget</c> / <c>updateTable</c>: a revision replaces the row of its number; anything else (new,
    /// clone) is added on top. Unlike legacy, the row keeps every field of the answer (legacy drops the flags and the
    /// total of a new row until the page is reloaded, so its menu misses items).
    /// </summary>
    public static List<BudgetListItemResponse> WithSaved(IReadOnlyList<BudgetListItemResponse> rows, BudgetResponse saved)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(saved);
        var row = ToRow(saved);
        if (saved.Revision > 0)
        {
            return [.. rows.Select(r => r.BudgetNumber == saved.BudgetNumber ? row : r)];
        }

        return [row, .. rows];
    }

    /// <summary>A list row of a full budget answer (legacy <c>buildBudgetObject</c>, plus the flags the menu needs).</summary>
    public static BudgetListItemResponse ToRow(BudgetResponse budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return new BudgetListItemResponse(budget.Id, budget.BudgetNumber, budget.Client?.Name, budget.Descriptive, budget.PersonInCharge,
            budget.MadeBy?.Name, budget.Date, budget.Revision, budget.Complete, budget.WasPdfDownloaded, budget.HasGenerateJob, budget.Total,
            budget.DaysNeededToDoTheWork);
    }

    /// <summary>Legacy <c>new Date(date).toLocaleDateString("pt-pt")</c>: <c>dd/MM/yyyy</c> in the browser's zone.</summary>
    public static string Date(DateTime? utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return utc is { } value ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Invalid Date";
    }

    /// <summary>
    /// Legacy <c>copyToClipboardClicked</c>. Without a job: <c>\\DIB4T\Orçamentos\{year of the budget date}\{client}\{num} - {desc}</c>.
    /// With a job: <c>D:\NAS\Obras\{year}\{client}\{num} - {desc}</c>, the year being the job's start year (or
    /// <c>20</c> + the number's first two digits when the job is not known), plus <c>\CAD</c> while the job is in
    /// development, approval or approved, <c>\CAM</c> in machining.
    /// </summary>
    public static string CopyPath(BudgetListItemResponse budget, TimeZoneInfo zone, JobFolder? job = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(zone);
        var folder = $"{budget.ClientName}\\{budget.BudgetNumber} - {budget.Descriptive}";
        if (!budget.HasGenerateJob)
        {
            var year = budget.Date is { } date ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(date, DateTimeKind.Utc), zone).Year.ToString(CultureInfo.InvariantCulture) : "NaN";
            return $"\\\\DIB4T\\Orçamentos\\{year}\\{folder}";
        }

        var jobYear = job?.StartDate is { } start
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), zone).Year.ToString(CultureInfo.InvariantCulture)
            : "20" + (budget.BudgetNumber is { Length: >= 2 } number ? number[..2] : string.Empty);
        var root = $"D:\\NAS\\Obras\\{jobYear}\\{folder}";
        return job?.State switch
        {
            JobStates.Development or JobStates.OnApproval or JobStates.Approved => root + "\\CAD",
            JobStates.Machining => root + "\\CAM",
            _ => root,
        };
    }

    /// <summary>
    /// The row menu (legacy <c>budgetOptions.jsx</c>), in its order: "Obter pdf" (complete), "Detalhes obra" and
    /// "Imprimir" (has a job), "Clonar" (always), "Nova revisão" (complete, no job), "Criar obra" (complete, PDF
    /// downloaded, no job). <paramref name="available"/> leaves out the actions of later steps ("Detalhes obra" and
    /// "Imprimir": Phase 4) until they exist.
    /// </summary>
    public static IReadOnlyList<BudgetMenuItem> Menu(bool complete, bool wasPdfDownloaded, bool hasJob, ISet<BudgetMenuItem>? available = null)
    {
        var items = new List<BudgetMenuItem>();
        if (complete)
        {
            items.Add(BudgetMenuItem.Pdf);
        }

        if (hasJob)
        {
            items.Add(BudgetMenuItem.JobDetails);
            items.Add(BudgetMenuItem.Print);
        }

        items.Add(BudgetMenuItem.Clone);
        if (complete && !hasJob)
        {
            items.Add(BudgetMenuItem.NewRevision);
        }

        if (complete && wasPdfDownloaded && !hasJob)
        {
            items.Add(BudgetMenuItem.CreateJob);
        }

        available ??= Available;
        return [.. items.Where(available.Contains)];
    }

    /// <summary>The menu actions this version of the app has.</summary>
    public static ISet<BudgetMenuItem> Available { get; } =
        new HashSet<BudgetMenuItem> { BudgetMenuItem.Pdf, BudgetMenuItem.Clone, BudgetMenuItem.NewRevision, BudgetMenuItem.CreateJob };

    /// <summary>Legacy <c>generatePdff</c>: <c>Prop - {number}.pdf</c>, <c>Prop - {number} R{revision}.pdf</c> for a revision.</summary>
    public static string PdfFileName(string? budgetNumber, double revision) =>
        revision != 0 ? $"Prop - {budgetNumber} R{JsNumber.ToString(revision)}.pdf" : $"Prop - {budgetNumber}.pdf";

    /// <summary>Legacy <c>updatedRowAfterPdfDownload</c>: the row of that budget shows the PDF as downloaded.</summary>
    public static List<BudgetListItemResponse> WithPdfDownloaded(IReadOnlyList<BudgetListItemResponse> rows, string id)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Select(r => r.Id == id ? r with { WasPdfDownloaded = true } : r)];
    }

    /// <summary>
    /// After "Criar obra": the row (or revision) the job was made from shows it has a job (its menu changes, the
    /// "contem obra" icon appears). Legacy goes to the jobs board instead, which comes with the next step.
    /// </summary>
    public static List<BudgetListItemResponse> WithJob(IReadOnlyList<BudgetListItemResponse> rows, string id)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Select(r => r.Id == id ? r with { HasGenerateJob = true } : r)];
    }

    /// <summary>The legacy label of a menu item.</summary>
    public static string Label(BudgetMenuItem item) => item switch
    {
        BudgetMenuItem.Pdf => "Obter pdf",
        BudgetMenuItem.JobDetails => "Detalhes obra",
        BudgetMenuItem.Print => "Imprimir",
        BudgetMenuItem.Clone => "Clonar",
        BudgetMenuItem.NewRevision => "Nova revisão",
        _ => "Criar obra",
    };
}

/// <summary>The row menu's actions.</summary>
public enum BudgetMenuItem
{
    Pdf,
    JobDetails,
    Print,
    Clone,
    NewRevision,
    CreateJob,
}

/// <summary>What the copy path needs of a budget's job (legacy <c>getByBudgetNumber</c>: start date and state).</summary>
public sealed record JobFolder(DateTime? StartDate, string? State);
