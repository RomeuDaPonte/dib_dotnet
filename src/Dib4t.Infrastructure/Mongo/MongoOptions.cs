using System.ComponentModel.DataAnnotations;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// The new app's own database (<c>mongo:9.0.8</c>, PLAN.md §2.2, §12). Never the legacy server. Configured with
/// <c>Mongo__ConnectionString</c>; the database is the one named in the connection string (default <c>dib4t</c>).
/// </summary>
public sealed class MongoOptions
{
    public const string SectionName = "Mongo";
    public const string DefaultDatabaseName = "dib4t";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;
}
