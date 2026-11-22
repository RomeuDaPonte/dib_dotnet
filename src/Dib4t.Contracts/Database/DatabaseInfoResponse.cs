using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Database;

/// <summary>
/// <c>GET /api/database/info</c> 200 body, legacy shape and key order (<c>getInfo</c> + <c>busyWith</c>, recorded in
/// <c>characterization/06-database.json</c>). A statistic the server does not report is left out, as legacy's
/// <c>undefined</c>; <see cref="BusyWith"/> is always written (<c>null</c>, <c>"backup"</c> or <c>"restauro"</c>).
/// </summary>
public sealed record DatabaseInfoResponse(
    string Name,
    string Host,
    int Port,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerVersion,
    int Collections,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? AvgObjSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DataSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StorageSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Indexes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? IndexSize,
    IReadOnlyList<CollectionInfoResponse> CollectionsInfo,
    DateTime FetchedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BusyWith);

/// <summary>One row of <see cref="DatabaseInfoResponse.CollectionsInfo"/>; without <c>collStats</c> only the count and the number of indexes.</summary>
public sealed record CollectionInfoResponse(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? AvgObjSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StorageSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Indexes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? IndexSize);
