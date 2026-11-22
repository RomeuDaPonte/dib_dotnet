namespace Dib4t.Application.Database;

/// <summary>
/// The legacy response texts of the backup/restore endpoints, byte for byte (<c>routes/database.js</c>,
/// <c>helpers/backupFormat.js</c>, <c>repositories/databaseRepository.js</c>; recorded in
/// <c>tests/fixtures/legacy/characterization/06-database.json</c>).
/// </summary>
public static class BackupMessages
{
    public const string NoFileReceived = "Nenhum ficheiro de backup recebido.";
    public const string NotAValidBackup = "O ficheiro não é um backup válido da Dib4t.";
    public const string UnexpectedDataAfterEnd = "Dados inesperados após o fim do backup.";
    public const string Incomplete = "O ficheiro de backup está incompleto.";
    public const string MissingHeader = "O ficheiro de backup não tem cabeçalho.";
    public const string DuplicateHeader = "Cabeçalho duplicado no backup.";
    public const string CorruptedHeader = "Ficheiro de backup corrompido (cabeçalho).";
    public const string NewerVersion = "Este backup foi criado por uma versão mais recente da aplicação.";
    public const string CorruptedCollection = "Ficheiro de backup corrompido (coleção).";
    public const string InvalidCollection = "Coleção inválida no backup.";
    public const string DocumentWithoutCollection = "Documento sem coleção no backup.";
    public const string CorruptedDocument = "Documento corrompido no backup.";
    public const string CorruptedEnd = "Ficheiro de backup corrompido (fim).";
    public const string UnknownFrame = "Ficheiro de backup corrompido.";
    public const string CountsMismatch = "O ficheiro de backup está corrompido (contagens não coincidem).";
    public const string RestoreFailed = "Erro ao restaurar o backup.";
    public const string BackupFailed = "Erro ao criar o backup.";

    /// <summary>The 409 text while another backup or restore runs; <paramref name="operation"/> is "backup" or "restauro".</summary>
    public static string Busy(string operation) => $"Já existe uma operação em curso ({operation}). Tente mais tarde.";
}
