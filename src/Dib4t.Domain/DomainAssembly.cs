using System.Reflection;

namespace Dib4t.Domain;

/// <summary>Marker for the Dib4t.Domain assembly (architecture tests, DI scanning).</summary>
public static class DomainAssembly
{
    public static Assembly Assembly { get; } = typeof(DomainAssembly).Assembly;
}
