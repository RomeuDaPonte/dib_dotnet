namespace Dib4t.E2E.Harness;

/// <summary>
/// Settings of the E2E harness (PLAN.md §11.7), from environment variables:
/// <list type="bullet">
/// <item><c>DIB4T_E2E_SEED</c>: the seed backup loaded into both databases (default: the anonymized production fixture).</item>
/// <item><c>DIB4T_E2E_REUSE_LEGACY=1</c>: use a legacy app that is already running (<c>legacy.sh up</c>) instead of
/// starting one. Its database is REPLACED by the seed backup and it is left running afterwards.</item>
/// <item><c>DIB4T_E2E_HEADED=1</c>: show the browser.</item>
/// <item><c>DIB4T_LEGACY_ROOT</c>: folder with the legacy <c>back-end/</c> and <c>front-end/</c> (default: the parent of the repo).</item>
/// </list>
/// </summary>
public sealed record HarnessOptions(
    string RepoRoot,
    string SeedBackup,
    bool ReuseLegacy,
    bool Headed,
    string ArtifactsDirectory)
{
    /// <summary>Docker volume of the legacy DB during an E2E run; never the dev volume <c>dib4t-legacy-db</c>.</summary>
    public const string LegacyE2EVolume = "dib4t-legacy-e2e";

    /// <summary>Same image as production (PLAN.md §2.2).</summary>
    public const string NewMongoImage = "mongo:9.0.8";

    public string LegacyScript => Path.Combine(RepoRoot, "tools", "legacy", "legacy.sh");

    public static HarnessOptions FromEnvironment()
    {
        var repoRoot = FindRepoRoot();
        var seed = Environment.GetEnvironmentVariable("DIB4T_E2E_SEED") is { Length: > 0 } s
            ? Path.GetFullPath(s)
            : Path.Combine(repoRoot, "tests", "fixtures", "legacy", "prod-2026-11-11.anon.dibbak");
        if (!File.Exists(seed))
        {
            throw new FileNotFoundException($"E2E seed backup not found: {seed}", seed);
        }

        return new HarnessOptions(
            repoRoot,
            seed,
            Environment.GetEnvironmentVariable("DIB4T_E2E_REUSE_LEGACY") == "1",
            Environment.GetEnvironmentVariable("DIB4T_E2E_HEADED") == "1",
            Path.Combine(repoRoot, "TestResults", "e2e-artifacts"));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dib4t.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Dib4t.slnx not found above " + AppContext.BaseDirectory);
    }
}
