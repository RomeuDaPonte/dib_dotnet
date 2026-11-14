using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using NetArchTest.Rules;

namespace Dib4t.Architecture.Tests;

/// <summary>PLAN.md §3.1, at the compiled-type level (catches what project files cannot, e.g. a forbidden namespace in code).</summary>
public sealed class TypeDependencyTests
{
    public static TheoryData<string, string[]> ForbiddenDependencies => new()
    {
        { "Dib4t.SharedKernel", ["Dib4t.Domain", "Dib4t.Application", "Dib4t.Infrastructure", "Dib4t.Contracts", "Dib4t.Client", "Dib4t.Server", "MongoDB", "Microsoft.AspNetCore", "System.Net.Http"] },
        { "Dib4t.Domain", ["Dib4t.Application", "Dib4t.Infrastructure", "Dib4t.Contracts", "Dib4t.Client", "Dib4t.Server", "MongoDB", "Microsoft.AspNetCore", "System.Net.Http"] },
        { "Dib4t.Application", ["Dib4t.Infrastructure", "Dib4t.Contracts", "Dib4t.Client", "Dib4t.Server", "MongoDB", "Microsoft.AspNetCore"] },
        { "Dib4t.Contracts", ["Dib4t.Domain", "Dib4t.Application", "Dib4t.Infrastructure", "Dib4t.Client", "Dib4t.Server", "MongoDB"] },
        { "Dib4t.Client", ["Dib4t.Domain", "Dib4t.Application", "Dib4t.Infrastructure", "Dib4t.Server", "MongoDB"] },
    };

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Assembly_has_no_forbidden_dependency(string assembly, string[] forbidden) =>
        ShouldPass(Types.InAssembly(Solution.Assemblies[assembly]).ShouldNot().HaveDependencyOnAny(forbidden));

    [Fact]
    public void Client_components_do_not_use_HttpClient() =>
        // Components inject a state/view-model service, which uses a typed API client (§3.1).
        ShouldPass(Types.InAssembly(Solution.Assemblies["Dib4t.Client"])
            .That().Inherit(typeof(ComponentBase))
            .ShouldNot().HaveDependencyOn("System.Net.Http.HttpClient"));

    [Fact]
    public void Controllers_do_not_use_infrastructure_or_MongoDB() =>
        // Controllers bind, call an application service and map the Result (§3.1).
        ShouldPass(Types.InAssembly(Solution.Assemblies["Dib4t.Server"])
            .That().Inherit(typeof(ControllerBase))
            .ShouldNot().HaveDependencyOnAny("Dib4t.Infrastructure", "MongoDB"));

    [Theory]
    [InlineData("Task")]
    [InlineData("Entity")]
    public void No_type_collides_with_framework_names(string name) =>
        // Use BudgetTask and BusinessEntity (§3.1).
        ShouldPass(Types.InAssemblies(Solution.Assemblies.Values).ShouldNot().HaveName(name));

    [Fact]
    public void Every_assembly_has_version_1_0_0() =>
        // ADR-0003: set once in Directory.Build.props, shown in Settings → Info, without a "+commit" suffix.
        Solution.Assemblies.Values
            .Select(assembly => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            .Should().AllBe("1.0.0");

    private static void ShouldPass(ConditionList rule)
    {
        var result = rule.GetResult();
        result.IsSuccessful.Should().BeTrue("these types break the rule: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
