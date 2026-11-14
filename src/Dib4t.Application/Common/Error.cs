using System.Diagnostics.CodeAnalysis;

namespace Dib4t.Application.Common;

/// <summary>What kind of failure a use case returns; controllers map it to the legacy status code (PLAN.md §3.2, §6).</summary>
public enum ErrorKind
{
    /// <summary>400: invalid input (Joi errors, rejected files).</summary>
    Validation,

    /// <summary>404.</summary>
    NotFound,

    /// <summary>409: e.g. another backup/restore is running.</summary>
    Conflict,

    /// <summary>400 with a business message (e.g. "Orçamento gerou obra!").</summary>
    BusinessRule,

    /// <summary>500 with a use-case specific legacy message (e.g. "Erro ao restaurar o backup.").</summary>
    Failure,
}

/// <summary>A typed use-case error. <see cref="Message"/> is the exact legacy (Portuguese) response text.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C# only; the name of PLAN.md §3.2.")]
public sealed record Error(ErrorKind Kind, string Message)
{
    public static Error Validation(string message) => new(ErrorKind.Validation, message);

    public static Error NotFound(string message) => new(ErrorKind.NotFound, message);

    public static Error Conflict(string message) => new(ErrorKind.Conflict, message);

    public static Error BusinessRule(string message) => new(ErrorKind.BusinessRule, message);

    public static Error Failure(string message) => new(ErrorKind.Failure, message);
}
