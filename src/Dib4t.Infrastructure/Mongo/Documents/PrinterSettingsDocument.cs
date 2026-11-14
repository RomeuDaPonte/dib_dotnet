using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>printersettings</c> (legacy <c>models/printerSettings.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class PrinterSettingsDocument
{
    public ObjectId Id { get; set; }

    public string? Ip { get; set; }

    public double? Port { get; set; }

    /// <summary>One of <see cref="SharedKernel.Vocabulary.PrintTemplates"/>.</summary>
    public string? TemplateName { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
