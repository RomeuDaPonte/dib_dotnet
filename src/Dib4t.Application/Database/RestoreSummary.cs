using System.Text.Json;

namespace Dib4t.Application.Database;

/// <summary>
/// What a restore reports (legacy <c>restoreBackup</c> result). <see cref="BackupCreatedAt"/> and
/// <see cref="BackupDatabase"/> are the header's raw JSON values, echoed as they are (legacy sends
/// <c>header.createdAt</c> as parsed, so a missing value is left out and any JSON type is kept).
/// </summary>
public sealed record RestoreSummary(JsonElement? BackupCreatedAt, JsonElement? BackupDatabase, int Collections, long Documents);
