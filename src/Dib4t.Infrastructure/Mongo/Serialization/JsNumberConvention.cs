using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace Dib4t.Infrastructure.Mongo.Serialization;

/// <summary>Every <see cref="double"/>/<c>double?</c> member is a JavaScript number (<see cref="JsNumberSerializer"/>).</summary>
internal sealed class JsNumberConvention : ConventionBase, IMemberMapConvention
{
    public void Apply(BsonMemberMap memberMap)
    {
        ArgumentNullException.ThrowIfNull(memberMap);
        if (memberMap.MemberType == typeof(double))
        {
            memberMap.SetSerializer(JsNumberSerializer.Instance);
        }
        else if (memberMap.MemberType == typeof(double?))
        {
            memberMap.SetSerializer(JsNumberSerializer.Nullable);
        }
    }
}
