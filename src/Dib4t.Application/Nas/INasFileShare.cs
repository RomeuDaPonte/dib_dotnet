using System.Globalization;

namespace Dib4t.Application.Nas;

/// <summary>
/// The NAS folder of one budget (legacy <c>serverDefaultBudgetPath</c>, <c>models/budget.js</c>, PLAN.md §5):
/// <c>/share/Orçamentos/{year}/{client}/{number} - {descriptive}</c>. Names are used byte for byte (no trimming, no
/// Unicode normalization); a <c>/</c> in an existing descriptive makes nested folders, as in legacy (D21).
/// </summary>
/// <param name="Year">The creation year (create, clone: the current year; rename: the year of the budget's date).</param>
/// <param name="ClientName">The client's name.</param>
/// <param name="BudgetNumber"><c>YY-NNNN</c>.</param>
/// <param name="Descriptive">The budget's descriptive.</param>
public sealed record BudgetFolder(int Year, string ClientName, string BudgetNumber, string Descriptive)
{
    public const string BudgetsRoot = "/share/Orçamentos";

    /// <summary>The absolute folder path (the contract of <c>tests/fixtures/legacy/ssh</c>, "ops").</summary>
    public string Path => string.Create(CultureInfo.InvariantCulture, $"{BudgetsRoot}/{Year}/{ClientName}/{BudgetNumber} - {Descriptive}");
}

/// <summary>The outcome of a NAS folder operation; the adapter has logged the details (category <c>Nas</c>).</summary>
public enum NasResult
{
    /// <summary>Done (or, with the NAS off, only logged).</summary>
    Done,

    /// <summary>Create: every command ran, but the budget folder is not there afterwards (legacy "Erro ao criar pasta").</summary>
    FolderMissing,

    /// <summary>The NAS could not be reached, refused the login or a command failed (legacy: an unhandled rejection).</summary>
    Failed,
}

/// <summary>
/// The NAS folder operations of the budget routes (PLAN.md §5, §5.1). In Production with <c>Nas:Enabled</c> they run
/// over SSH; everywhere else they only log the commands they would run (§4.7, D5).
/// </summary>
public interface INasFileShare
{
    /// <summary><c>false</c> when the operations are only logged (not Production, or <c>Nas:Enabled=false</c>).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Budget create and clone: the folder with <c>Geometria</c> and <c>Documentos</c> (<c>mkdir -p</c>), then checks
    /// that the folder exists.
    /// </summary>
    Task<NasResult> CreateBudgetFolderAsync(BudgetFolder folder, CancellationToken cancellationToken);

    /// <summary>Budget edit (revision 0, other descriptive): <c>mv</c> of the folder to its new name.</summary>
    Task<NasResult> RenameBudgetFolderAsync(BudgetFolder current, BudgetFolder renamed, CancellationToken cancellationToken);
}
