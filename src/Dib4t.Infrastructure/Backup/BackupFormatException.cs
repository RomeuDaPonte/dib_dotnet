namespace Dib4t.Infrastructure.Backup;

/// <summary>The file is not a valid backup; <see cref="Exception.Message"/> is the legacy text (<c>BackupFormatError</c>).</summary>
internal sealed class BackupFormatException(string message) : Exception(message);
