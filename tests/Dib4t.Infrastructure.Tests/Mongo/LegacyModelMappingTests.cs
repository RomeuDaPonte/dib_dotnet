using System.Reflection;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.SharedKernel.Vocabulary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Dib4t.Infrastructure.Tests.Mongo;

/// <summary>
/// Class maps of the 8 legacy collections (PLAN.md §4.2–§4.5): every real legacy document reads, and writing it back
/// changes nothing except the Mongoose defaults of missing fields and stored <c>null</c>s of fields the app leaves
/// out when null; BSON types follow §4.3; insert field order is the legacy one.
/// </summary>
public sealed class LegacyModelMappingTests
{
    private static readonly ObjectId SomeId = ObjectId.Parse("650000000000000000000001");

    [Fact]
    public void Every_production_document_round_trips_with_only_the_expected_changes()
    {
        var changes = LegacyDocuments.Production
            .SelectMany(collection => collection.Value.SelectMany(document =>
                BsonChange.Between(document, LegacyDocuments.RoundTrip(collection.Key, document))
                    .Select(change => change with { Path = collection.Key + "." + change.Path })))
            .ToList();

        changes.Where(change => change.Kind is BsonChangeKind.Changed or BsonChangeKind.Lost or BsonChangeKind.Retyped)
            .Should().BeEmpty("values, types and unknown fields must survive (§4.3 rules 1, 7)");

        // Counts from the data-shape inventory (docs/data/legacy-data-shapes.md, §4.5 items 2–3).
        changes.GroupBy(change => (change.Kind, change.Path))
            .ToDictionary(group => $"{group.Key.Kind} {group.Key.Path}", group => group.Count())
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["AddedDefault jobs.commentType"] = 934,
                ["AddedDefault users.fieldOfCompetencies"] = 1,
                ["DroppedNull jobs.deliveryDate"] = 126,
                ["DroppedNull jobs.costNonQuality"] = 1,
                ["DroppedNull jobs.jobStatisticsDates.devEndedInDate"] = 1,
            });
    }

    [Theory]
    [InlineData("00-initial-db.json")]
    [InlineData("99-final-db.json")]
    public void Documents_written_by_the_legacy_app_round_trip_unchanged(string file)
    {
        foreach (var (collection, documents) in LegacyDocuments.Characterization(file))
        {
            foreach (var document in documents)
            {
                BsonChange.Between(document, LegacyDocuments.RoundTrip(collection, document))
                    .Should().BeEmpty($"{collection} {document["_id"]}");
            }
        }
    }

    /// <summary>
    /// Documents the legacy app just inserted (characterization run, before any later <c>$set</c> appended fields):
    /// written back, they have exactly the same field order, also inside the embedded statistics. Edited documents
    /// keep their own order because existing documents are only ever changed with <c>$set</c> (§4.3 rule 3); a whole
    /// rewrite would reorder 1,136 production budgets and 707 jobs whose fields were appended over time. 15,950
    /// production tasks have <c>taskType</c> before <c>budgetId</c>: the order depends on the legacy create route
    /// (budget revision/clone vs. task create), which the budgets slice reproduces per route.
    /// </summary>
    [Theory]
    [InlineData("budgets", 2)]
    [InlineData("entities", 0)]
    [InlineData("jobs", 0)]
    [InlineData("tasks", 0)]
    [InlineData("users", 0)]
    [InlineData("prices", 0)]
    [InlineData("paymentmethods", 0)]
    [InlineData("printersettings", 0)]
    public void Insert_field_order_is_the_legacy_one(string collection, int index)
    {
        var inserted = LegacyDocuments.Characterization("99-final-db.json")[collection][index];

        var written = LegacyDocuments.RoundTrip(collection, inserted);

        Names(written).Should().Equal(Names(inserted));
        foreach (var embedded in inserted.Elements.Where(element => element.Value.IsBsonDocument))
        {
            Names(written[embedded.Name].AsBsonDocument).Should().Equal(Names(embedded.Value.AsBsonDocument), embedded.Name);
        }
    }

    [Fact]
    public void A_new_budget_is_written_with_the_mongoose_defaults_and_legacy_types()
    {
        var written = LegacyDocuments.Write(new BudgetDocument
        {
            Id = SomeId,
            Client = SomeId,
            Descriptive = "Máquina de corte",
            MadeBy = SomeId,
            BudgetNumber = "26-0001",
            PaymentMethod = "Transferência a 30 dias",
            Total = 12345.67,
            Date = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc),
        });

        Shape(written).Should().Equal(
            "_id:ObjectId", "revision:Int32=0", "complete:Boolean=false", "wasPdfDownloaded:Boolean=false",
            "margin:Int32=30", "daysNeededToDoTheWork:Int32=60", "validity:Int32=30", "hasGenerateJob:Boolean=false",
            "client:ObjectId", "descriptive:String", "madeBy:ObjectId", "budgetNumber:String", "paymentMethod:String",
            "total:Double=12345.67", "date:DateTime", "__v:Int32=0");
    }

    [Fact]
    public void A_new_job_is_written_with_the_mongoose_defaults_and_no_priority()
    {
        var written = LegacyDocuments.Write(new JobDocument
        {
            Id = SomeId,
            Budget = SomeId,
            DueDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            StartDate = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            DevStatistic = new JobDevStatisticDocument { BudgetedHours = 12.5, Deviation = 12.5 },
            MachiningStatistic = new JobMachiningStatisticDocument(),
            AssemblyStatistic = new JobAssemblyStatisticDocument { BudgetedHours = 8, Deviation = 8 },
        });

        Shape(written).Should().Equal(
            "_id:ObjectId", "invoicedPercent:Int32=0", "finalPrice:Int32=1", "state:String=\"Desenvolvimento\"",
            "orderIdentification:String=\"Por preencher\"", "isOfficialOrderIdentification:Boolean=false",
            "components:Int32=0", "automation:Int32=0", "materials:Int32=0", "others:Int32=0", "costNonQuality:Int32=0",
            "commentType:Array=[]", "budget:ObjectId", "dueDate:DateTime", "startDate:DateTime",
            "devStatistic:Document", "machiningStatistic:Document", "assemblyStatistic:Document", "__v:Int32=0");
        Shape(written["devStatistic"].AsBsonDocument).Should().Equal(
            "numberOfHoursUsed:Int32=0", "budgetedHours:Double=12.5", "deviation:Double=12.5", "_id:ObjectId");
        Shape(written["machiningStatistic"].AsBsonDocument).Should().Equal(
            "numberOfHoursUsed:Int32=0", "budgetedHours:Int32=0", "deviation:Int32=0",
            "verificationAndShapeControl:String=\"n/a\"", "dimensionalVerificationAndControl:String=\"n/a\"",
            "analysisOfTheDegreeOfFinish:String=\"n/a\"", "_id:ObjectId");
        Shape(written["assemblyStatistic"].AsBsonDocument).Should().Equal(
            "numberOfHoursUsed:Int32=0", "budgetedHours:Int32=8", "deviation:Int32=8", "pneumaticCircuits:String=\"n/a\"",
            "electricalBoardsAndCircuits:String=\"n/a\"", "kinematics:String=\"n/a\"", "programmingFeaturesPLC:String=\"n/a\"",
            "operator_equipmentSafetyConditions:String=\"n/a\"", "visualInspection:String=\"n/a\"",
            "validTryOnFirstTest:String=\"1\"", "_id:ObjectId");
    }

    [Fact]
    public void Each_new_embedded_document_gets_its_own_id()
    {
        var first = new JobDevStatisticDocument();
        var second = new JobDevStatisticDocument();
        var dates = new JobStatisticsDatesDocument();

        new[] { first.Id, second.Id, dates.Id }.Should().OnlyHaveUniqueItems().And.NotContain(ObjectId.Empty);
        Shape(LegacyDocuments.Write(dates)).Should().Equal("_id:ObjectId");
    }

    [Fact]
    public void A_new_user_starts_with_no_roles_and_the_default_field_of_competencies()
    {
        var written = LegacyDocuments.Write(new UserDocument { Id = SomeId, Name = "Ana", Email = "ana@dib4t.local", Password = "$2b$10$x" });

        Shape(written).Should().Equal(
            "_id:ObjectId", "roles:Array=[]", "fieldOfCompetencies:String=\"Desenvolvimento\"", "name:String", "email:String",
            "password:String", "__v:Int32=0");
    }

    [Fact]
    public void Missing_fields_read_as_the_mongoose_defaults()
    {
        var job = LegacyDocuments.Read<JobDocument>(BsonDocument.Parse("""
            { "_id": { "$oid": "650000000000000000000001" }, "devStatistic": { "_id": { "$oid": "650000000000000000000002" } },
              "machiningStatistic": { }, "assemblyStatistic": { } }
            """));

        job.State.Should().Be(JobStates.Development);
        job.FinalPrice.Should().Be(1);
        job.OrderIdentification.Should().Be("Por preencher");
        job.CostNonQuality.Should().Be(0);
        job.CommentType.Should().BeEmpty();
        job.Priority.Should().BeNull("missing priority stays missing (§4.4.3)");
        job.JobStatisticsDates.Should().BeNull();
        job.DevStatistic!.Deviation.Should().Be(0);
        job.MachiningStatistic!.AnalysisOfTheDegreeOfFinish.Should().Be(QuestionResponses.NotApplicable);
        job.AssemblyStatistic!.ValidTryOnFirstTest.Should().Be("1");
        job.MachiningStatistic.Id.Should().NotBe(ObjectId.Empty, "an embedded document without _id gets a new one");
        job.MachiningStatistic.Id.Should().NotBe(job.AssemblyStatistic.Id);

        var budget = LegacyDocuments.Read<BudgetDocument>(BsonDocument.Parse("""{ "_id": { "$oid": "650000000000000000000001" } }"""));
        (budget.Margin, budget.DaysNeededToDoTheWork, budget.Validity, budget.Revision).Should().Be((30, 60, 30, 0));
        (budget.HasGenerateJob, budget.Complete, budget.WasPdfDownloaded).Should().Be((false, false, false));
        budget.Total.Should().BeNull();

        var user = LegacyDocuments.Read<UserDocument>(BsonDocument.Parse("""{ "_id": { "$oid": "650000000000000000000001" } }"""));
        user.Roles.Should().BeEmpty();
        user.FieldOfCompetencies.Should().Be(FieldsOfCompetence.Default);
    }

    [Fact]
    public void Missing_lists_are_not_shared_between_documents()
    {
        var empty = BsonDocument.Parse("""{ "_id": { "$oid": "650000000000000000000001" } }""");
        var first = LegacyDocuments.Read<JobDocument>(empty);
        var second = LegacyDocuments.Read<JobDocument>(empty);

        first.CommentType.Add(CommentTypes.Other);

        second.CommentType.Should().BeEmpty();
    }

    [Fact]
    public void Older_shapes_read_and_write_back_unchanged()
    {
        var original = BsonDocument.Parse("""
            {
              "_id": { "$oid": "650000000000000000000001" },
              "invoicedPercent": { "$numberLong": "100" },
              "finalPrice": { "$numberDouble": "1234.5" },
              "materials": { "$numberLong": "5000000000" },
              "state": "Faturada",
              "priority": "Média",
              "deliveryDate": { "$date": { "$numberLong": "-61470604800000" } },
              "startDate": { "$date": "1969-12-31T23:59:59.999Z" },
              "comments": "  com espaços\t ",
              "isHightPriority": true,
              "devStatistic": { "numberOfHoursUsed": 0.5, "budgetedHours": 1, "deviation": -0.5,
                                "_id": { "$oid": "650000000000000000000002" }, "legacyNote": "kept" },
              "__v": 3
            }
            """);

        var written = LegacyDocuments.RoundTrip(MongoCollectionNames.Jobs, original);

        BsonChange.Between(original, written).Where(change => change.Kind != BsonChangeKind.AddedDefault)
            .Should().BeEquivalentTo([
                new BsonChange(BsonChangeKind.Retyped, "invoicedPercent", "Int64 → Int32"),
                new BsonChange(BsonChangeKind.Retyped, "materials", "Int64 → Double"),
            ], "Int64 is read and written the way the legacy driver writes numbers; nothing else changes");
        written["deliveryDate"].ToUniversalTime().Year.Should().Be(22);
        written["isHightPriority"].Should().Be(BsonBoolean.True);
        written["devStatistic"]["legacyNote"].Should().Be(new BsonString("kept"));
    }

    [Fact]
    public void Null_roles_and_non_nfc_strings_are_kept_as_stored()
    {
        var decomposed = "Técnico"; // "Técnico" with a combining accent (not NFC)
        var original = new BsonDocument
        {
            { "_id", SomeId },
            { "roles", new BsonArray { BsonNull.Value, RoleNames.CanSeeObras, BsonNull.Value } },
            { "fieldOfCompetencies", JobStates.Assembly },
            { "name", decomposed },
            { "email", " utilizador@dib4t.local " },
            { "password", "$2a$10$abcdefghijklmnopqrstuv" },
            { "__v", 0 },
        };

        var written = LegacyDocuments.RoundTrip(MongoCollectionNames.Users, original);

        BsonChange.Between(original, written).Should().BeEmpty();
        written["name"].AsString.Should().Be(decomposed);
    }

    [Fact]
    public void Typed_updates_and_filters_use_the_legacy_number_types()
    {
        MongoMappings.Register();
        var serializer = BsonSerializer.LookupSerializer<JobDocument>();
        var args = new RenderArgs<JobDocument>(serializer, BsonSerializer.SerializerRegistry);

        var update = Builders<JobDocument>.Update
            .Set(job => job.FinalPrice, 10.0)
            .Set(job => job.Materials, 0.5)
            .Set(job => job.DevStatistic!.NumberOfHoursUsed, 3.0)
            .Set(job => job.DeliveryDate, null)
            .Inc(job => job.Version, 1)
            .Render(args).AsBsonDocument;
        var filter = Builders<JobDocument>.Filter.Where(job => job.FinalPrice > 100.0 && job.Priority == JobPriorities.High)
            .Render(args);

        update.ToJson().Should().Be(BsonDocument.Parse("""
            { "$set": { "finalPrice": 10, "materials": 0.5, "devStatistic.numberOfHoursUsed": 3, "deliveryDate": null },
              "$inc": { "__v": 1 } }
            """).ToJson());
        update["$set"]["finalPrice"].BsonType.Should().Be(BsonType.Int32);
        update["$set"]["materials"].BsonType.Should().Be(BsonType.Double);
        filter.ToJson().Should().Be("""{ "finalPrice" : { "$gt" : 100 }, "priority" : "Alta" }""");
        filter["finalPrice"]["$gt"].BsonType.Should().Be(BsonType.Int32);
    }

    [Fact]
    public void Every_collection_has_a_document_class()
    {
        MongoCollectionNames.ByDocumentType.Values.Should().BeEquivalentTo(
            LegacyDocuments.Production.Keys, "the 8 collections of the production backup (§4.1)");
    }

    /// <summary>§4.3 rule 1: numbers are <c>double</c> (never decimal, float, long); only <c>__v</c> is an Int32.</summary>
    [Fact]
    public void Document_classes_use_only_legacy_compatible_member_types()
    {
        var allowed = new[]
        {
            typeof(ObjectId), typeof(ObjectId?), typeof(string), typeof(bool), typeof(double), typeof(double?),
            typeof(DateTime?), typeof(List<string?>), typeof(BsonDocument),
        };
        var documentTypes = typeof(BudgetDocument).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(BudgetDocument).Namespace && type.IsClass && type.IsPublic)
            .ToList();
        var offending = documentTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => !(property.Name == "Version" && property.PropertyType == typeof(int)))
                .Where(property => !allowed.Contains(property.PropertyType) && !documentTypes.Contains(property.PropertyType))
                .Select(property => $"{type.Name}.{property.Name}: {property.PropertyType.Name}"))
            .ToList();

        documentTypes.Should().HaveCount(12);
        offending.Should().BeEmpty();
    }

    private static List<string> Names(BsonDocument document) => [.. document.Names];

    private static List<string> Shape(BsonDocument document) =>
    [
        .. document.Elements.Select(element => element.Value.BsonType switch
        {
            BsonType.ObjectId or BsonType.DateTime or BsonType.Document => $"{element.Name}:{element.Value.BsonType}",
            BsonType.String when element.Value.AsString.Length > 16 || element.Name is "name" or "email" or "password"
                or "descriptive" or "budgetNumber" => $"{element.Name}:String",
            _ => $"{element.Name}:{element.Value.BsonType}={element.Value.ToJson()}",
        }),
    ];
}
