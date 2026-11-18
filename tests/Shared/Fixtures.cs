namespace Dib4t.Tests.Common;

/// <summary>Paths of the committed legacy fixtures (<c>tests/fixtures/legacy</c>).</summary>
public static class Fixtures
{
    public static string Root { get; } = FindRoot();

    /// <summary>Anonymized production backup of 2026-11-11 (30,754 documents, PLAN.md §10 Phase 0).</summary>
    public static string ProductionBackup => Path.Combine(Root, "legacy", "prod-2026-11-11.anon.dibbak");

    public static string Characterization(string file) => Path.Combine(Root, "legacy", "characterization", file);

    /// <summary>Login contract answers of the legacy code (Joi, isemail, bcryptjs, jsonwebtoken; tools/legacy/auth-golden.js).</summary>
    public static string AuthGolden => Path.Combine(Root, "legacy", "auth", "auth-golden.json");

    /// <summary>
    /// Users, prices, payment methods and printer settings contract answers of the legacy code (Joi, Mongoose casts and
    /// saves; tools/legacy/settings-golden.js).
    /// </summary>
    public static string SettingsGolden => Path.Combine(Root, "legacy", "settings", "settings-golden.json");

    /// <summary>Entity contract answers of the legacy code (Joi, Mongoose casts and saves, search patterns; tools/legacy/entity-golden.js).</summary>
    public static string EntityGolden => Path.Combine(Root, "legacy", "entities", "entity-golden.json");

    /// <summary>
    /// Budget list/create/clone/revision/edit contract answers of the legacy code (Joi, repository queries and rows,
    /// numbering, Mongoose documents and saves; tools/legacy/budget-golden.js).
    /// </summary>
    public static string BudgetGolden => Path.Combine(Root, "legacy", "budgets", "budget-golden.json");

    /// <summary>
    /// Budget details contract answers of the legacy code (the task routes, patchProperty and import run verbatim:
    /// Joi, Mongoose casts and saves; tools/legacy/budget-details-golden.js).
    /// </summary>
    public static string BudgetDetailsGolden => Path.Combine(Root, "legacy", "budget-details", "budget-details-golden.json");

    /// <summary>
    /// Job create contract answers of the legacy code (POST /api/job run verbatim: Joi, Mongoose casts and saves) and
    /// the label payloads (tools/legacy/job-golden.js).
    /// </summary>
    public static string JobGolden => Path.Combine(Root, "legacy", "jobs", "job-golden.json");

    /// <summary>
    /// The legacy pdfmake's breaks, shaping and laid-out pages of the budget PDF (tools/legacy/pdf-golden.js): line-breaks,
    /// shaping, layouts (11 reference samples + synthetic budgets), digests (every budget of the anonymized backup).
    /// </summary>
    public static string PdfLayout(string file) => Path.Combine(Root, "legacy", "pdf-layout", file);

    /// <summary>The reference PDFs made by the legacy app (tools/legacy/pdf-samples.js) and their manifest.</summary>
    public static string PdfSample(string file) => Path.Combine(Root, "legacy", "pdf", file);

    /// <summary>
    /// Approved page images of the new app's PDF for the reference budgets D22/D23 change (ADR-0005); see the README there.
    /// </summary>
    public static string PdfApproved => Path.Combine(Root, "pdf-approved");

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
