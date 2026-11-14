namespace Dib4t.Tests.Common;

/// <summary>Paths of the committed legacy fixtures (<c>tests/fixtures/legacy</c>).</summary>
public static class Fixtures
{
    public static string Root { get; } = FindRoot();

    /// <summary>Anonymized production backup of 2026-11-11 (30,754 documents, PLAN.md §10 Phase 0).</summary>
    public static string ProductionBackup => Path.Combine(Root, "legacy", "prod-2026-11-11.anon.dibbak");

    public static string Characterization(string file) => Path.Combine(Root, "legacy", "characterization", file);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures");
            if (Directory.Exists(Path.Combine(candidate, "legacy")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/fixtures not found above " + AppContext.BaseDirectory);
    }
}
