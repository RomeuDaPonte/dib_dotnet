using Dib4t.Infrastructure.Mongo.Documents;
using Dib4t.Infrastructure.Mongo.Serialization;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace Dib4t.Infrastructure.Mongo;

/// <summary>
/// Class maps of the 8 legacy collections (PLAN.md §4.1–§4.4), registered once per process before the first use of
/// a document type. Conventions, for the <see cref="Documents"/> namespace only: camelCase element names, a member
/// that is <c>null</c> is left out (Mongoose does not write <c>undefined</c>), numbers through
/// <see cref="JsNumberSerializer"/>. From the driver's default conventions: unknown fields are kept in the
/// <c>ExtraElements</c> member (§4.3 rule 7), and a field missing in the BSON keeps the property initializer, which
/// is the Mongoose default (§4.4 rule 2; both covered by tests). No attributes on the documents, no indexes, no
/// validators (§4.3 rule 8).
/// </summary>
public static class MongoMappings
{
    private const string ConventionName = "Dib4t legacy model";
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            var documentsNamespace = typeof(BudgetDocument).Namespace;
            ConventionRegistry.Register(
                ConventionName,
                new ConventionPack
                {
                    new CamelCaseElementNameConvention(),
                    new IgnoreIfNullConvention(true),
                    new JsNumberConvention(),
                },
                type => type.Namespace == documentsNamespace);

            Root<BudgetDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<BusinessEntityDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<BudgetTaskDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<UserDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<PricesDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<PaymentMethodDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<PrinterSettingsDocument>(cm => cm.GetMemberMap(d => d.Version));
            Root<JobDocument>(cm => cm.GetMemberMap(d => d.Version));

            Embedded<JobDevStatisticDocument>(cm => cm.GetMemberMap(d => d.Id));
            Embedded<JobMachiningStatisticDocument>(cm => cm.GetMemberMap(d => d.Id));
            Embedded<JobAssemblyStatisticDocument>(cm =>
            {
                cm.GetMemberMap(d => d.ProgrammingFeaturesPlc).SetElementName("programmingFeaturesPLC");
                cm.GetMemberMap(d => d.OperatorEquipmentSafetyConditions).SetElementName("operator_equipmentSafetyConditions");
                return cm.GetMemberMap(d => d.Id);
            });
            Embedded<JobStatisticsDatesDocument>(cm => cm.GetMemberMap(d => d.Id));

            _registered = true;
        }
    }

    /// <summary>A collection document: <c>_id</c> first (generated on insert), <c>__v</c> = <paramref name="version"/>.</summary>
    private static void Root<TDocument>(Func<BsonClassMap<TDocument>, BsonMemberMap> version) =>
        BsonClassMap.RegisterClassMap<TDocument>(cm =>
        {
            cm.AutoMap();
            version(cm).SetElementName("__v");
        });

    /// <summary>
    /// An embedded document: its <c>_id</c> is an ordinary field in declaration order (the legacy app writes it after
    /// the defaulted fields); the property initializer gives a new id, also to an old document that lacks one (§4.3 rule 5).
    /// </summary>
    private static void Embedded<TDocument>(Func<BsonClassMap<TDocument>, BsonMemberMap> id) =>
        BsonClassMap.RegisterClassMap<TDocument>(cm =>
        {
            cm.AutoMap();
            cm.SetIdMember(null);
            id(cm).SetElementName("_id");
        });
}
