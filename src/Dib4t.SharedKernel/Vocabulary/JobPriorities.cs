namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>
/// Job priorities (legacy <c>helpers/jobPriority.js</c>). The legacy schema default resolves to <c>undefined</c>, so a
/// job can have no priority (PLAN.md §4.2, §4.4.3).
/// </summary>
public static class JobPriorities
{
    public const string High = "Alta";
    public const string Medium = "Média";
    public const string Low = "Baixa";

    public static IReadOnlyList<string> All { get; } = [High, Medium, Low];
}
