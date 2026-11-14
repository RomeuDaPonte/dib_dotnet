namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>Budget task types (legacy <c>helpers/taskType.js</c>), stored in <c>tasks.taskType</c>.</summary>
public static class TaskTypes
{
    public const string Automation = "automation";
    public const string Consultancy = "consultancy";
    public const string Development = "development";
    public const string Machining = "machining";
    public const string Assembly = "assembly";
    public const string Others = "others";
    public const string Components = "components";

    /// <summary>In the legacy schema's enum order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Automation, Consultancy, Development, Machining, Assembly, Others, Components];
}
