namespace Dib4t.Application.Common;

/// <summary>Legacy texts shared by several use cases.</summary>
public static class LegacyMessages
{
    /// <summary>
    /// D16 (PLAN.md §9): the answer to every failure the legacy app leaves to Express's error page (a failed
    /// Mongoose save or cast, an exception); the intended legacy text of <c>middleware/error.js</c>.
    /// </summary>
    public const string SomethingFailed = "Something failed.";

    /// <summary>Legacy routes' own 500 text when a delete reports no success.</summary>
    public const string ServerError = "Erro no servidor!!";
}
