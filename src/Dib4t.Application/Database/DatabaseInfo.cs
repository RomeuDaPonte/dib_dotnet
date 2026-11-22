namespace Dib4t.Application.Database;

/// <summary>
/// What <c>GET /api/database/info</c> reports (legacy <c>getInfo</c> in <c>repositories/databaseRepository.js</c>):
/// <c>dbStats</c>, <c>buildInfo</c> and one <see cref="CollectionInfo"/> per user collection. Numbers are the server's
/// as JavaScript numbers (legacy promotes every BSON number); a value the server leaves out is <see langword="null"/>
/// and is left out of the answer, as <c>JSON.stringify</c> drops <c>undefined</c>.
/// </summary>
public sealed record DatabaseInfo(
    string Name,
    string Host,
    int Port,
    string? ServerVersion,
    int Collections,
    double? Documents,
    double? AvgObjSize,
    double? DataSize,
    double? StorageSize,
    double? Indexes,
    double? IndexSize,
    IReadOnlyList<CollectionInfo> CollectionsInfo);

/// <summary>
/// One collection (legacy <c>getCollectionStats</c>): <c>collStats</c>, or, if that command fails, only
/// <c>estimatedDocumentCount</c> and the number of indexes (<see cref="Size"/>, <see cref="AvgObjSize"/>,
/// <see cref="StorageSize"/> and <see cref="IndexSize"/> then <see langword="null"/>). <see cref="AvgObjSize"/> is
/// <c>0</c> when <c>collStats</c> has none (an empty collection: legacy <c>s.avgObjSize || 0</c>).
/// </summary>
public sealed record CollectionInfo(
    string Name,
    double? Count,
    double? Size,
    double? AvgObjSize,
    double? StorageSize,
    double? Indexes,
    double? IndexSize);

/// <summary>What a written backup holds (legacy <c>writeBackup</c> result).</summary>
public sealed record BackupSummary(int Collections, long Documents);

/// <summary><see cref="DatabaseInfo"/> with the moment it was read and the running operation (legacy <c>{ ...info, busyWith }</c>).</summary>
public sealed record DatabaseInfoSnapshot(DatabaseInfo Info, DateTime FetchedAt, string? BusyWith);
