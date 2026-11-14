using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dib4t.Contracts.Database;

/// <summary>
/// <c>POST /api/database/restore</c> 200 body, legacy shape <c>{ backupCreatedAt, backupDatabase, collections, documents }</c>.
/// The two header values are echoed as they are in the backup's header (left out when the header has none).
/// </summary>
public sealed record RestoreResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BackupCreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BackupDatabase,
    int Collections,
    long Documents);
