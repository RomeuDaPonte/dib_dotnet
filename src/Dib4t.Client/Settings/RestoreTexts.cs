using Dib4t.Contracts.Database;

namespace Dib4t.Client.Settings;

/// <summary>
/// The texts of a restore's progress and result (new app only, PLAN.md §7.6): the server's stage in the overlay, and
/// the result dialog with the built-in verification, the adjustments for MongoDB 9.0 and the automatic pre-restore
/// backup.
/// </summary>
public static class RestoreTexts
{
    public const string SafetyNote = "Antes de substituir os dados, o servidor guarda automaticamente uma cópia da base de dados atual.";
    public const string Validating = "A verificar o ficheiro de backup...";
    public const string PreRestoreBackup = "A guardar uma cópia da base de dados atual...";
    public const string Verifying = "A verificar os dados restaurados...";
    public const string Completed = "Restauro concluído";
    public const string CompletedWithDifferences = "Restauro concluído com diferenças";
    public const string Failed = "O restauro falhou";
    public const string NoDifferences = "Verificação: a base de dados é igual ao ficheiro (coleções, documentos e índices).";
    public const string Differences = "Verificação: foram encontradas diferenças entre a base de dados e o ficheiro:";
    public const string AdjustmentsTitle = "Ajustes para o MongoDB 9.0 (os documentos não foram alterados):";
    public const string Unchanged = "A base de dados não foi alterada.";

    /// <summary>The overlay's message and percent for a running restore's stage.</summary>
    public static (string Message, int? Percent) Stage(RestoreStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Stage switch
        {
            RestoreStatusResponse.Validating => (Validating, null),
            RestoreStatusResponse.PreRestoreBackupStage => (PreRestoreBackup, null),
            RestoreStatusResponse.Replacing => (Replacing(status), status.Documents is > 0 and var total
                ? (int)Math.Floor((status.DocumentsDone / (double)total * 100) + 0.5)
                : null),
            RestoreStatusResponse.Verifying => (Verifying, null),
            _ => (DatabaseTabMessages.Replacing, null),
        };
    }

    private static string Replacing(RestoreStatusResponse status) =>
        status.CurrentCollection is { } collection && status.Collections is { } collections
            ? $"A substituir os dados: {collection} ({DatabaseFormat.Number(status.CollectionsDone)} de {DatabaseFormat.Number(collections)} coleções, {DatabaseFormat.Number(status.DocumentsDone)} de {DatabaseFormat.Number(status.Documents ?? 0)} documentos)."
            : DatabaseTabMessages.Replacing;

    /// <summary>The result dialog's title.</summary>
    public static string Title(RestoreStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Stage != RestoreStatusResponse.Completed ? Failed
            : status.Verification is { HasDifferences: true } ? CompletedWithDifferences
            : Completed;
    }

    /// <summary>What the restore wrote (a completed restore).</summary>
    public static string Summary(RestoreStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return $"{DatabaseFormat.Number(status.Collections ?? 0)} coleções, {DatabaseFormat.Number(status.Documents ?? 0)} documentos.";
    }

    /// <summary>One line per difference the verification found.</summary>
    public static IReadOnlyList<string> DifferenceLines(RestoreVerificationResponse verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var lines = new List<string>();
        foreach (var c in verification.Collections.Where(c => !c.Matches))
        {
            if (!c.Exists)
            {
                lines.Add($"{c.Name}: a coleção não existe");
                continue;
            }

            if (c.Documents != c.ExpectedDocuments)
            {
                lines.Add($"{c.Name}: {DatabaseFormat.Number(c.Documents)} de {DatabaseFormat.Number(c.ExpectedDocuments)} documentos");
            }
            else if (!c.DocumentsMatch)
            {
                lines.Add($"{c.Name}: o conteúdo dos documentos é diferente");
            }

            if (c.MissingIndexes.Count > 0)
            {
                lines.Add($"{c.Name}: índices em falta: {string.Join(", ", c.MissingIndexes)}");
            }
        }

        lines.AddRange(verification.UnexpectedCollections.Select(name => $"{name}: coleção que não está no ficheiro"));
        return lines;
    }

    /// <summary>One line per adjustment for MongoDB 9.0.</summary>
    public static string Adjustment(RestoreAdjustmentResponse adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        var text = (adjustment.Index, adjustment.IndexRemoved) switch
        {
            (null, _) => $"{adjustment.Collection}: opções não usadas: {string.Join(", ", adjustment.Removed)}",
            (var index, true) => $"{adjustment.Collection}: índice {index} não criado",
            (var index, false) => $"{adjustment.Collection}, índice {index}: campos não usados: {string.Join(", ", adjustment.Removed)}",
        };
        return adjustment.ServerMessage is { } refusal ? $"{text} (recusado pelo servidor: {refusal})" : text;
    }

    /// <summary>Where the automatic copy of the database as it was before is, and what a failure left.</summary>
    public static string? PreRestoreBackupLine(RestoreStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.PreRestoreBackup is { } file
            ? status.DatabaseChanged
                ? $"A base de dados ficou incompleta. A cópia automática da base de dados anterior é {file} (pasta de backups do servidor)."
                : $"Cópia automática da base de dados anterior: {file} (pasta de backups do servidor)."
            : null;
    }
}

/// <summary>Texts shared by the tab and <see cref="RestoreTexts"/>.</summary>
public static class DatabaseTabMessages
{
    /// <summary>Legacy: while the server replaces the data (the upload is complete).</summary>
    public const string Replacing = "A substituir os dados. Isto pode demorar alguns minutos.";
}
