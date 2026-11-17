using System.Diagnostics.CodeAnalysis;
using Dib4t.Application.Nas;

namespace Dib4t.Infrastructure.Nas;

/// <summary>
/// The shell commands of the NAS folder operations (legacy <c>helpers/pathHlelper.js</c>, <c>models/budget.js</c>).
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

        commands = new BudgetFolderCommands(makeDirectories, $"if test -d {quoted}; then echo 1; else echo 0; fi");
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

    private static string Quote(string value) =>
        TryQuote(value, out var quoted) ? quoted : throw new ArgumentException("A NUL cannot be passed to sh.", nameof(value));
}

/// <summary>The commands of one budget folder creation.</summary>
internal sealed record BudgetFolderCommands(IReadOnlyList<string> MakeDirectories, string Check);
