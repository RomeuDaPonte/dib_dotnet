using Dib4t.Application.Account;
using Dib4t.Domain.Users;
using Dib4t.Infrastructure.Mongo.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Mongo.Repositories;

/// <summary><c>users</c> reads (legacy <c>repositories/userRepository.js</c>). Writes come with the users slice (Phase 2).</summary>
internal sealed class MongoUserRepository(IMongoDatabase database) : IUserRepository
{
    private readonly IMongoCollection<UserDocument> _users = database.Collection<UserDocument>();

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

    private static User ToUser(UserDocument document) => new(
        document.Id.ToString(),
        document.Name,
        document.Email,
        document.Password,
        document.Roles,
        document.FieldOfCompetencies);
}
