using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Mongo.Documents;

/// <summary><c>users</c> (legacy <c>models/user.js</c>, PLAN.md §4.2). Conventions: see <see cref="BudgetDocument"/>.</summary>
public sealed class UserDocument
{
    public ObjectId Id { get; set; }

    /// <summary>
    /// <see cref="RoleNames"/>. Imported arrays contain <c>null</c> elements (§4.5 item 2): kept, skipped by role checks.
    /// </summary>
    public List<string?> Roles { get; set; } = [];

    public string FieldOfCompetencies { get; set; } = FieldsOfCompetence.Default;

    public string? Name { get; set; }

    public string? Email { get; set; }

    /// <summary>bcrypt hash (<c>$2a$</c>/<c>$2b$</c>, cost 10). Never logged.</summary>
    public string? Password { get; set; }

    public int Version { get; set; }

    public BsonDocument? ExtraElements { get; set; }
}
