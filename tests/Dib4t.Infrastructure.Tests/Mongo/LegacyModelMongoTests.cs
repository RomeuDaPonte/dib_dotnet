using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>
/// The class maps through the real driver on <c>mongo:9.0.8</c> (PLAN.md §4.3, §4.4): typed inserts and <c>$set</c>
/// updates store the legacy BSON types, and imported documents read through the typed collections.
/// </summary>
public sealed class LegacyModelMongoTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Typed_insert_and_set_store_the_legacy_bson_types()
    {
        MongoMappings.Register();
        var database = mongo.NewDatabase();
        var jobs = database.Collection<JobDocument>();
        var job = new JobDocument
        {
            Budget = ObjectId.GenerateNewId(),
            DueDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            StartDate = new DateTime(2026, 11, 14, 9, 30, 0, DateTimeKind.Utc),
            DevStatistic = new JobDevStatisticDocument { BudgetedHours = 12.5 },
        };

        await jobs.InsertOneAsync(job, cancellationToken: Cancellation);
        await jobs.UpdateOneAsync(
            Builders<JobDocument>.Filter.Eq(j => j.Id, job.Id),
            Builders<JobDocument>.Update.Set(j => j.FinalPrice, 2500.0).Set(j => j.Materials, 99.9).Set(j => j.DevStatistic!.NumberOfHoursUsed, 4.0),
            cancellationToken: Cancellation);

        job.Id.Should().NotBe(ObjectId.Empty, "the driver generates the root _id on insert");
        var stored = await database.GetCollection<BsonDocument>(MongoCollectionNames.Jobs).Find(new BsonDocument("_id", job.Id)).SingleAsync(Cancellation);
        stored.Names.First().Should().Be("_id");
        stored["finalPrice"].Should().Be(new BsonInt32(2500));
        stored["materials"].Should().Be(new BsonDouble(99.9));
        stored["invoicedPercent"].BsonType.Should().Be(BsonType.Int32);
        stored["devStatistic"]["numberOfHoursUsed"].Should().Be(new BsonInt32(4));
        stored["devStatistic"]["budgetedHours"].Should().Be(new BsonDouble(12.5));
        stored["devStatistic"]["_id"].BsonType.Should().Be(BsonType.ObjectId);
        stored["startDate"].ToUniversalTime().Should().Be(job.StartDate);
        stored.Contains("priority").Should().BeFalse();
        stored.Contains("deliveryDate").Should().BeFalse();
        stored["__v"].Should().Be(new BsonInt32(0));
    }

    [Fact]
    public async Task Imported_production_documents_read_through_the_typed_collections()
    {
        MongoMappings.Register();
        var database = mongo.NewDatabase();
        foreach (var (name, documents) in LegacyDocuments.Production)
        {
            await database.GetCollection<BsonDocument>(name).InsertManyAsync(documents, cancellationToken: Cancellation);
        }

        (await database.Collection<BudgetDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().HaveCount(3397);
        (await database.Collection<BusinessEntityDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().HaveCount(235);
        (await database.Collection<BudgetTaskDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().HaveCount(25971);
        (await database.Collection<PaymentMethodDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().HaveCount(20);
        (await database.Collection<PricesDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().ContainSingle();
        (await database.Collection<PrinterSettingsDocument>().Find(_ => true).ToListAsync(Cancellation)).Should().HaveCount(2);

        var jobs = await database.Collection<JobDocument>().Find(_ => true).ToListAsync(Cancellation);
        jobs.Should().HaveCount(1118);
        jobs.Count(j => j.ExtraElements?.Contains("isHightPriority") == true).Should().Be(7, "unknown fields are kept (§4.5 item 4)");
        jobs.Min(j => j.DeliveryDate).Should().HaveYear(22, "the typo year reads (§4.5 item 5)");

        var users = await database.Collection<UserDocument>().Find(_ => true).ToListAsync(Cancellation);
        users.Should().HaveCount(10);
        users.SelectMany(u => u.Roles).Count(role => role is null).Should().Be(30, "null roles are kept (§4.5 item 2)");

        // Numbers compare across Int32/Double in queries, as in the legacy app.
        (await database.Collection<BudgetTaskDocument>().CountDocumentsAsync(t => t.Quantity > 0.0 && t.Quantity < 1.0, cancellationToken: Cancellation))
            .Should().BePositive();
    }
}
