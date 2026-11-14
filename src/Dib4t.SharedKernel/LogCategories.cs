namespace Dib4t.SharedKernel;

/// <summary>
/// Categories of the log store (PLAN.md §3.3): every entry has exactly one. Shared with the client, which lists them
/// in the Registos filters.
/// </summary>
public static class LogCategories
{
    public const string Database = "Database";
    public const string Http = "Http";
    public const string Nas = "Nas";
    public const string Printer = "Printer";
    public const string App = "App";
    public const string Jobs = "Jobs";
    public const string Auth = "Auth";

    /// <summary>Name of the log property that sets the category explicitly (wins over the source-context mapping).</summary>
    public const string PropertyName = "Category";

    public static IReadOnlyList<string> All { get; } = [Database, Http, Nas, Printer, App, Jobs, Auth];
}
