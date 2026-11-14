using System.Reflection;
using System.Xml.Linq;

namespace Dib4t.Architecture.Tests;

/// <summary>The src projects as files (project/package references) and as compiled assemblies.</summary>
internal static class Solution
{
    public static readonly DirectoryInfo Root = FindRoot();

    public static readonly IReadOnlyDictionary<string, Assembly> Assemblies = new Dictionary<string, Assembly>
    {
        ["Dib4t.SharedKernel"] = SharedKernel.SharedKernelAssembly.Assembly,
        ["Dib4t.Domain"] = Domain.DomainAssembly.Assembly,
        ["Dib4t.Application"] = Application.ApplicationAssembly.Assembly,
        ["Dib4t.Infrastructure"] = Infrastructure.InfrastructureAssembly.Assembly,
        ["Dib4t.Contracts"] = Contracts.ContractsAssembly.Assembly,
        ["Dib4t.Client"] = typeof(Client.Routes).Assembly,
        ["Dib4t.Server"] = typeof(Server.Components.App).Assembly,
    };

    /// <summary>Every project under src/, keyed by name, with its direct project and package references.</summary>
    public static IReadOnlyDictionary<string, SourceProject> SourceProjects { get; } = LoadSourceProjects();

    private static Dictionary<string, SourceProject> LoadSourceProjects()
    {
        var projects = new Dictionary<string, SourceProject>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root.FullName, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(file);
            var projectReferences = xml.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);
            var packageReferences = xml.Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")!.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = Path.GetFileNameWithoutExtension(file);
            projects.Add(name, new SourceProject(name, projectReferences, packageReferences));
        }

        return projects;
    }

    private static DirectoryInfo FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dib4t.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Dib4t.slnx not found above " + AppContext.BaseDirectory);
    }
}

internal sealed record SourceProject(string Name, IReadOnlySet<string> ProjectReferences, IReadOnlySet<string> PackageReferences);
