namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>
/// Answers of the machining/assembly verification questions (legacy <c>helpers/jobQuestionResponse.js</c>). Default
/// <see cref="NotApplicable"/>.
/// </summary>
public static class QuestionResponses
{
    public const string Ok = "Ok";
    public const string NotOk = "Nok";
    public const string NotApplicable = "n/a";

    public static IReadOnlyList<string> All { get; } = [Ok, NotOk, NotApplicable];

    /// <summary><c>assemblyStatistic.validTryOnFirstTest</c>: stored as a <b>string</b>, default <c>"1"</c>.</summary>
    public static IReadOnlyList<string> FirstTestTries { get; } = ["1", "2", "3"];

    public const string DefaultFirstTestTry = "1";
}
