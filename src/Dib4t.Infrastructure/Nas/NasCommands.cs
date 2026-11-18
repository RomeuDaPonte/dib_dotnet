using System.Diagnostics.CodeAnalysis;
using Dib4t.Application.Nas;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// The shell commands of the NAS folder operations (legacy <c>helpers/pathHlelper.js</c>, <c>models/budget.js</c>,
/// <c>routes/job.js</c>).
/// They make the same folders, in the same order, as legacy (the <c>ops</c> of
/// <c>tests/fixtures/legacy/ssh/ssh-commands.json</c>), but every path is <b>one</b> single-quoted <c>sh</c> word
/// (PLAN.md §5 "SSH safety", D20): nothing in a name is expanded, and a <c>'</c> no longer breaks the command.
/// Paths are absolute, so the legacy <c>cd /;</c> is not needed.
/// </summary>
internal static class NasCommands
{
    /// <summary>The two sub-folders of a budget folder, created in this order.</summary>
    public static readonly IReadOnlyList<string> BudgetSubfolders = ["Geometria", "Documentos"];

    /// <summary>Create/clone: the <c>mkdir -p</c> commands, then the check (its output is <c>1</c> when the folder exists).</summary>
    public static bool TryCreateBudgetFolder(BudgetFolder folder, [NotNullWhen(true)] out BudgetFolderCommands? commands)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var path = folder.Path;
        commands = null;
        if (!TryQuote(path, out var quoted))
        {
            return false;
        }

        List<string> makeDirectories = [$"mkdir -p {quoted}"];
        foreach (var subfolder in BudgetSubfolders)
        {
            makeDirectories.Add($"mkdir -p {Quote($"{path}/{subfolder}")}");
        }

        commands = new BudgetFolderCommands(makeDirectories, CheckCommand(path));
        return true;
    }

    /// <summary>Rename (edit of revision 0): <c>mv</c> of the folder.</summary>
    public static bool TryRenameBudgetFolder(BudgetFolder current, BudgetFolder renamed, [NotNullWhen(true)] out string? command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(renamed);
        command = TryQuote(current.Path, out var source) && TryQuote(renamed.Path, out var target) ? $"mv {source} {target}" : null;
        return command is not null;
    }

    /// <summary>The sub-folders made under a job folder, in the legacy order.</summary>
    public static readonly IReadOnlyList<string> JobSubfolders = ["CAD", "CAD/Equipamento", "CAM"];

    /// <summary>
    /// Job create (legacy <c>moveBudgetFolderToJobs</c>): the three <c>mkdir -p</c>, the two <c>mv</c> (budget folder's
    /// <c>Documentos</c> → job folder's <c>Informações Técnicas</c>, <c>Geometria</c> → <c>CAD/Artigo</c>), then the two
    /// checks (D4: on the <b>absolute</b> paths; legacy tests paths relative to the SSH home and never waits for them).
    /// </summary>
    public static bool TryMoveToJob(BudgetFolder budget, JobFolder job, [NotNullWhen(true)] out JobFolderCommands? commands)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(job);
        commands = null;
        var (from, to) = (budget.Path, job.Path);
        if (!TryQuote(from, out _) || !TryQuote(to, out _))
        {
            return false;
        }

        var (documents, geometry) = ($"{to}/Informações Técnicas", $"{to}/CAD/Artigo");
        commands = new JobFolderCommands(
            [.. JobSubfolders.Select(subfolder => $"mkdir -p {Quote($"{to}/{subfolder}")}")],
            [$"mv {Quote($"{from}/Documentos")} {Quote(documents)}", $"mv {Quote($"{from}/Geometria")} {Quote(geometry)}"],
            [CheckCommand(documents), CheckCommand(geometry)]);
        return true;
    }

    /// <summary>
    /// <paramref name="value"/> as one <c>sh</c> word: inside single quotes every byte is literal, and a single quote
    /// is written as <c>'\''</c> (close, escaped quote, reopen). A NUL cannot be passed in a command at all.
    /// </summary>
    public static bool TryQuote(string value, [NotNullWhen(true)] out string? quoted)
    {
        ArgumentNullException.ThrowIfNull(value);
        quoted = value.Contains('\0', StringComparison.Ordinal) ? null : "'" + value.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
        return quoted is not null;
    }

    private static string CheckCommand(string path) => $"if test -d {Quote(path)}; then echo 1; else echo 0; fi";

    private static string Quote(string value) =>
        TryQuote(value, out var quoted) ? quoted : throw new ArgumentException("A NUL cannot be passed to sh.", nameof(value));
}

/// <summary>The commands of one budget folder creation.</summary>
internal sealed record BudgetFolderCommands(IReadOnlyList<string> MakeDirectories, string Check);

/// <summary>The commands of one job folder move: folders, moves, checks (each check prints <c>1</c> when the folder exists).</summary>
internal sealed record JobFolderCommands(IReadOnlyList<string> MakeDirectories, IReadOnlyList<string> Moves, IReadOnlyList<string> Checks)
{
    public IEnumerable<string> All => MakeDirectories.Concat(Moves).Concat(Checks);
}
