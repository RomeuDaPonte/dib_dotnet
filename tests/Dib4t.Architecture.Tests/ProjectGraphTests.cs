namespace Dib4t.Architecture.Tests;

/// <summary>
/// PLAN.md §3.1, at the project-file level:
/// SharedKernel ← Domain ← Application ← Infrastructure ← Server; SharedKernel ← Contracts ← Client; Contracts ← Server.
/// The Server also references the Client, because it hosts the WebAssembly app.
/// </summary>
public sealed class ProjectGraphTests
{
    private const string SharedKernel = "Dib4t.SharedKernel";
    private const string Domain = "Dib4t.Domain";
    private const string Application = "Dib4t.Application";
    private const string Infrastructure = "Dib4t.Infrastructure";
    private const string Contracts = "Dib4t.Contracts";
    private const string Client = "Dib4t.Client";
    private const string Server = "Dib4t.Server";

    /// <summary>Everything a project may depend on, directly or through other projects.</summary>
    private static readonly Dictionary<string, string[]> AllowedDependencies = new()
    {
        [SharedKernel] = [],
        [Domain] = [SharedKernel],
        [Application] = [Domain, SharedKernel],
        [Infrastructure] = [Application, Domain, SharedKernel],
        [Contracts] = [SharedKernel],
        [Client] = [Contracts, SharedKernel],
        [Server] = [Infrastructure, Application, Domain, SharedKernel, Contracts, Client],
    };

    /// <summary>Packages that belong to Infrastructure (adapters) only.</summary>
    private static readonly string[] InfrastructureOnlyPackages =
        ["MongoDB.Driver", "MongoDB.Bson", "SSH.NET", "PDFsharp", "BCrypt.Net-Next", "Microsoft.Data.Sqlite"];

    public static TheoryData<string> ProjectNames => [.. AllowedDependencies.Keys];

    [Fact]
    public void Every_src_project_has_a_dependency_rule() =>
        Solution.SourceProjects.Keys.Should().BeEquivalentTo(AllowedDependencies.Keys,
            "a new project under src/ needs its place in the dependency rules of PLAN.md §3.1");

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Project_depends_only_on_allowed_projects(string project) =>
        TransitiveReferences(project, []).Should().BeSubsetOf(AllowedDependencies[project]);

    [Theory]
    [InlineData(SharedKernel)]
    [InlineData(Domain)]
    public void Core_project_has_no_packages(string project) =>
        Solution.SourceProjects[project].PackageReferences.Should().BeEmpty("{0} stays free of frameworks and persistence", project);

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Adapter_packages_are_referenced_only_by_infrastructure(string project)
    {
        if (project == Infrastructure)
        {
            return;
        }

        Solution.SourceProjects[project].PackageReferences.Should().NotIntersectWith(InfrastructureOnlyPackages);
    }

    [Theory]
    [InlineData(SharedKernel)]
    [InlineData(Domain)]
    [InlineData(Application)]
    [InlineData(Contracts)]
    public void Inner_project_has_no_web_or_ui_packages(string project) =>
        Solution.SourceProjects[project].PackageReferences
            .Where(package => package.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("MudBlazor", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();

    private static HashSet<string> TransitiveReferences(string project, HashSet<string> path)
    {
        path.Should().NotContain(project, "project references must not form a cycle");
        path.Add(project);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in Solution.SourceProjects[project].ProjectReferences)
        {
            result.Add(reference);
            result.UnionWith(TransitiveReferences(reference, [.. path]));
        }

        return result;
    }
}
