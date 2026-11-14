namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>Label templates (legacy <c>helpers/printTemplates.js</c>), stored in <c>printersettings.templateName</c>.</summary>
public static class PrintTemplates
{
    public const string Producao = "producao";
    public const string Maquinacao = "maquinacao";

    public static IReadOnlyList<string> All { get; } = [Producao, Maquinacao];
}
