using System.Reflection;

namespace Dib4t.Contracts;

/// <summary>Marker for the Dib4t.Contracts assembly (architecture tests, DI scanning).</summary>
public static class ContractsAssembly
{
    public static Assembly Assembly { get; } = typeof(ContractsAssembly).Assembly;
}
