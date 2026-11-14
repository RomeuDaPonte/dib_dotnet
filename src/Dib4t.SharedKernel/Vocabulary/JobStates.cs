namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>Job states (legacy <c>helpers/jobStates.js</c>), stored as these exact Portuguese strings (PLAN.md §4.3).</summary>
public static class JobStates
{
    public const string Development = "Desenvolvimento";
    public const string OnApproval = "Em Aprovação";
    public const string Approved = "Aprovado";
    public const string Machining = "Maquinação";
    public const string Assembly = "Montagem";
    public const string ForInvoicing = "Para faturação";
    public const string Billed = "Faturada";
    public const string Deleted = "Apagada";

    /// <summary>In the legacy order (<c>getAllJobStates</c>, ids 1–8).</summary>
    public static IReadOnlyList<string> All { get; } =
        [Development, OnApproval, Approved, Machining, Assembly, ForInvoicing, Billed, Deleted];
}
