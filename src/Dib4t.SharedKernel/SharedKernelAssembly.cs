using System.Reflection;

namespace Dib4t.SharedKernel;

/// <summary>Marker for the Dib4t.SharedKernel assembly (architecture tests, DI scanning).</summary>
public static class SharedKernelAssembly
{
    public static Assembly Assembly { get; } = typeof(SharedKernelAssembly).Assembly;
}
