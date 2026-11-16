namespace Dib4t.SharedKernel;

/// <summary>
/// The levels stored in the log store (Serilog's names, PLAN.md §3.3), most severe first. Shared with the client,
/// which lists them in the Registos filters.
/// </summary>
public static class LogLevels
{
    public const string Fatal = "Fatal";
    public const string Error = "Error";
    public const string Warning = "Warning";
    public const string Information = "Information";
    public const string Debug = "Debug";
    public const string Verbose = "Verbose";

    public static IReadOnlyList<string> All { get; } = [Fatal, Error, Warning, Information, Debug, Verbose];
}
