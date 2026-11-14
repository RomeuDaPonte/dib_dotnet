using System.Reflection;

namespace Dib4t.Application;

/// <summary>Marker for the Dib4t.Application assembly (architecture tests, DI scanning).</summary>
public static class ApplicationAssembly
{
    public static Assembly Assembly { get; } = typeof(ApplicationAssembly).Assembly;
}
