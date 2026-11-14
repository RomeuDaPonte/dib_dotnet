using System.Reflection;

namespace Dib4t.Infrastructure;

/// <summary>Marker for the Dib4t.Infrastructure assembly (architecture tests, DI scanning).</summary>
public static class InfrastructureAssembly
{
    public static Assembly Assembly { get; } = typeof(InfrastructureAssembly).Assembly;
}
