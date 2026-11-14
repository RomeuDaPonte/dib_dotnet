namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>
/// <c>users.fieldOfCompetencies</c> (legacy user schema enum): the job states a user works in. Default
/// <see cref="JobStates.Development"/>.
/// </summary>
public static class FieldsOfCompetence
{
    public const string Default = JobStates.Development;

    public static IReadOnlyList<string> All { get; } =
        [JobStates.Development, JobStates.Machining, JobStates.Assembly, JobStates.ForInvoicing];
}
