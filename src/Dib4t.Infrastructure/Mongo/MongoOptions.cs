using System.ComponentModel.DataAnnotations;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// The new app's own database (<c>mongo:9.0.8</c>, PLAN.md §2.2, §12). Never the legacy server. Configured with
/// <c>Mongo__ConnectionString</c>; the database is the one named in the connection string (default <c>dib4t</c>).
/// Validated at startup.
/// </summary>
public sealed class MongoOptions
{
    public const string SectionName = "Mongo";
    public const string DefaultDatabaseName = "dib4t";

    /// <summary>Production and every test run on 9.0.x; another version only gets a startup warning.</summary>
    public const int TestedServerMajorVersion = 9;

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>How long the startup version check waits before trying an unreachable server again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan StartupCheckRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long startup waits for the default-data seeding (<see cref="StartupSeeding"/>) before the app starts
    /// without it and keeps trying in the background.
    /// </summary>
    public TimeSpan StartupSeedTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
