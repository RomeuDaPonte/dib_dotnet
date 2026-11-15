using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Dib4t.Infrastructure.Mongo.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary><c>users</c> (legacy <c>repositories/userRepository.js</c>, the user model's <c>update</c> + <c>save</c>).</summary>
internal sealed class MongoUserRepository(IMongoDatabase database) : IUserRepository
{
    private readonly IMongoCollection<UserDocument> _users = database.Collection<UserDocument>();
    private readonly IMongoCollection<BsonDocument> _raw = database.GetCollection<BsonDocument>(MongoCollectionNames.Users);

    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        // Mongoose findOne: no sort, so the first match in natural order, like the legacy app.
        var document = await _users.Find(Builders<UserDocument>.Filter.Eq(u => u.Email, email))
            .Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToUser(document);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out var objectId))
        {
            return null;
        }

        var document = await _users.Find(Builders<UserDocument>.Filter.Eq(u => u.Id, objectId))
            .Limit(1).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : ToUser(document);
    }

    public async Task<IReadOnlyList<User>> ListByNameAsync(CancellationToken cancellationToken)
    {
        // Legacy find().select("name email roles fieldOfCompetencies").sort("name"): the hash never leaves the database.
        var projection = Builders<UserDocument>.Projection
            .Include(u => u.Name).Include(u => u.Email).Include(u => u.Roles).Include(u => u.FieldOfCompetencies);
        var documents = await _users.Find(FilterDefinition<UserDocument>.Empty)
            .Project<UserDocument>(projection)
            .Sort(Builders<UserDocument>.Sort.Ascending(u => u.Name))
            .ToListAsync(cancellationToken);
        return [.. documents.Select(ToUser)];
    }

    public async Task<string> InsertAsync(NewUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        // Legacy insert order: _id, roles, fieldOfCompetencies, name, email, password, __v (the document's property order).
        var document = new UserDocument
        {
            Id = ObjectId.GenerateNewId(),
            Roles = [.. user.Roles],
            FieldOfCompetencies = user.FieldOfCompetencies,
            Name = user.Name,
            Email = user.Email,
            Password = user.PasswordHash,
            Version = 0,
        };
        await _users.InsertOneAsync(document, cancellationToken: cancellationToken);
        return document.Id.ToString();
    }

    public async Task<bool> UpdateAsync(string id, UserUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!ObjectId.TryParse(id, out var objectId))
        {
            return false;
        }

        var filter = Builders<BsonDocument>.Filter.Eq("_id", objectId);
        var stored = await _raw.Find(filter).Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return false;
        }

        // user.update(body): name, email, roles, fieldOfCompetencies in this order; then the hash when one was sent.
        var assignments = new List<(string, BsonValue?)>
        {
            ("name", update.Name),
            ("email", update.Email),
            ("roles", update.Roles is null ? null : new BsonArray(update.Roles.Select(role => role is null ? BsonNull.Value : (BsonValue)role))),
            ("fieldOfCompetencies", update.FieldOfCompetencies),
        };
        if (update.PasswordHash is not null)
        {
            assignments.Add(("password", update.PasswordHash));
        }

        if (MongooseSave.Update(stored, assignments) is { } changes)
        {
            var result = await _raw.UpdateOneAsync(filter, changes, cancellationToken: cancellationToken);
            return result.MatchedCount > 0;
        }

        return true;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out var objectId))
        {
            return false;
        }

        var result = await _users.DeleteOneAsync(Builders<UserDocument>.Filter.Eq(u => u.Id, objectId), cancellationToken);
        return result.DeletedCount > 0;
    }

    private static User ToUser(UserDocument document) => new(
        document.Id.ToString(),
        document.Name,
        document.Email,
        document.Password,
        document.Roles,
        document.FieldOfCompetencies);
}
