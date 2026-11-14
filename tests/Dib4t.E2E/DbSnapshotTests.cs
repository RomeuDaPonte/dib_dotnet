using Dib4t.E2E.Harness;
using MongoDB.Bson;

namespace Dib4t.E2E;

/// <summary>The snapshot comparison used by the legacy comparison (PLAN.md §11.7) reports BSON-level differences.</summary>
public sealed class DbSnapshotTests
{
    private static readonly ObjectId Id = ObjectId.Parse("5f0c8a1b2c3d4e5f60718293");

    private static DbSnapshot Snapshot(params BsonDocument[] budgets) =>
        DbSnapshot.From(new Dictionary<string, IEnumerable<BsonDocument>> { ["budgets"] = budgets });

    [Fact]
    public void Equal_documents_have_no_differences() =>
        Snapshot(new BsonDocument { { "_id", Id }, { "total", 10 } })
            .DiffFrom(Snapshot(new BsonDocument { { "_id", Id }, { "total", 10 } }))
            .Should().BeEmpty();

    [Fact]
    public void Int32_and_Double_with_the_same_value_differ() =>
        Snapshot(new BsonDocument { { "_id", Id }, { "total", 10 } })
            .DiffFrom(Snapshot(new BsonDocument { { "_id", Id }, { "total", 10.0 } }))
            .Should().ContainSingle().Which.Should().Contain("budgets").And.Contain("total").And.Contain("Int32").And.Contain("Double");

    [Fact]
    public void Nested_value_and_array_length_differences_are_reported_with_their_path()
    {
        var left = Snapshot(new BsonDocument { { "_id", Id }, { "client", new BsonDocument("name", "A") }, { "tags", new BsonArray { 1, 2 } } });
        var right = Snapshot(new BsonDocument { { "_id", Id }, { "client", new BsonDocument("name", "B") }, { "tags", new BsonArray { 1 } } });

        left.DiffFrom(right).Should().HaveCount(2)
            .And.Contain(d => d.Contains(" client.name: String \"A\" vs String \"B\"", StringComparison.Ordinal))
            .And.Contain(d => d.Contains("tags: array length 2 vs 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_fields_documents_and_field_order_are_differences()
    {
        var other = ObjectId.Parse("5f0c8a1b2c3d4e5f60718294");
        var left = Snapshot(new BsonDocument { { "_id", Id }, { "a", 1 }, { "b", 2 } }, new BsonDocument("_id", other));
        var right = Snapshot(new BsonDocument { { "_id", Id }, { "b", 2 }, { "a", 1 }, { "c", BsonNull.Value } });

        left.DiffFrom(right).Should().HaveCount(3)
            .And.Contain(d => d.Contains(" c: missing vs Null", StringComparison.Ordinal))
            .And.Contain(d => d.Contains("(document): field order [_id,a,b] vs [_id,b,a]", StringComparison.Ordinal))
            .And.Contain(d => d.Contains("only on the left side", StringComparison.Ordinal));
    }

    [Fact]
    public void Seed_fixture_reads_with_every_collection()
    {
        var seed = DibbakFile.Read(HarnessOptions.FromEnvironment().SeedBackup);

        seed.Collections.Select(c => c.Name).Should().BeEquivalentTo(
            "budgets", "entities", "jobs", "paymentmethods", "prices", "printersettings", "tasks", "users");
        seed.DocumentCount.Should().Be(30_754);
    }
}
