using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Poc.Application.Authorization;
using Poc.Infrastructure.SpiceDb;

namespace Poc.UnitTests.Architecture;

/// <summary>
/// Layering rules from the PRD: Poc.Domain references nothing; Poc.Application references only Poc.Domain;
/// Authzed.Net is referenced by Poc.Infrastructure alone.
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = Assembly.Load("Poc.Domain");
    private static readonly Assembly Application = typeof(IAuthorizer).Assembly;
    private static readonly Assembly Infrastructure = typeof(SpiceDbConnection).Assembly;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Poc.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Poc.sln not found above the test output directory.");
    }

    /// <summary>Project and package references declared in each src project file.</summary>
    private static Dictionary<string, (string[] Projects, string[] Packages)> SourceProjects()
    {
        var result = new Dictionary<string, (string[], string[])>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(file);
            var projects = xml.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/'))).ToArray();
            var packages = xml.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToArray();
            result[Path.GetFileNameWithoutExtension(file)] = (projects, packages);
        }

        return result;
    }

    [Fact]
    public void Domain_project_references_nothing()
    {
        var (projects, packages) = SourceProjects()["Poc.Domain"];
        Assert.Empty(projects);
        Assert.Empty(packages);
    }

    [Fact]
    public void Application_project_references_only_Domain()
    {
        var (projects, packages) = SourceProjects()["Poc.Application"];
        Assert.Equal(["Poc.Domain"], projects);
        Assert.Empty(packages);
    }

    [Fact]
    public void Only_Infrastructure_references_Authzed_Net()
    {
        var offenders = SourceProjects().Where(p => p.Value.Packages.Contains("Authzed.Net")).Select(p => p.Key).ToList();
        Assert.Equal(["Poc.Infrastructure"], offenders);
    }

    [Fact]
    public void No_source_project_other_than_Infrastructure_uses_grpc_or_authzed_types()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var result = Types.InAssembly(assembly).ShouldNot()
                .HaveDependencyOnAny("Authzed", "Grpc", "Google.Protobuf", "Poc.Infrastructure").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Domain_types_depend_on_no_other_project()
    {
        var result = Types.InAssembly(Domain).ShouldNot().HaveDependencyOnAny("Poc.Application", "Poc.Infrastructure").GetResult();
        Assert.True(result.IsSuccessful);
        Assert.DoesNotContain(Domain.GetReferencedAssemblies(), a => a.Name!.StartsWith("Poc.", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_assembly_references_no_Infrastructure()
    {
        Assert.DoesNotContain(Application.GetReferencedAssemblies(), a => a.Name == "Poc.Infrastructure");
    }

    [Fact]
    public void Infrastructure_adapter_does_reference_the_engine_client_so_the_rules_above_are_meaningful()
    {
        Assert.Contains(Infrastructure.GetReferencedAssemblies(), a => a.Name == "Authzed.Net");
    }

    /// <summary>
    /// PRD rule 6: Commerce code never writes tuples. Only these types may touch the relationship store: the projector
    /// and Access Management (the two writers), reconciliation repair and the start-up reset (both named in the PRD), and
    /// the adapter that implements it. Anything new must be added here deliberately.
    /// </summary>
    [Fact]
    public void Only_the_named_components_may_depend_on_the_relationship_store()
    {
        var allowed = new[]
        {
            "Poc.Infrastructure.SpiceDb.SpiceDbRelationshipStore",
            "Poc.Infrastructure.Projection.OutboxProjector",
            "Poc.Application.Access.AccessService",
            "Poc.Application.Reconciliation.ReconciliationService",
            "Poc.Infrastructure.Startup.StartupSequence",
            // Composition root: these only register the interface in the container; they never call it.
            "Poc.Infrastructure.SpiceDb.ServiceCollectionExtensions",
            "Poc.Infrastructure.InfrastructureServiceCollectionExtensions",
        };
        var store = typeof(IRelationshipStore).FullName!;

        foreach (var assembly in new[] { Domain, Application, Infrastructure })
        {
            var dependents = Types.InAssembly(assembly).That().HaveDependencyOn(store).GetTypes()
                .Select(t => t.FullName!)
                .Where(n => !n.Contains('+') && !n.Contains('<')) // skip compiler-generated nested types
                .Where(n => n != store)
                .Order().ToList();
            Assert.Empty(dependents.Except(allowed));
        }
    }

    [Fact]
    public void The_read_side_of_access_management_can_only_read_tuples()
    {
        // AccessQueryService lists grants and group membership. It takes IRelationshipReader, so it cannot write one.
        var result = Types.InAssembly(Application).That().HaveName("AccessQueryService")
            .ShouldNot().HaveDependencyOn(typeof(IRelationshipStore).FullName!).GetResult();
        Assert.True(result.IsSuccessful);
        Assert.Single(Types.InAssembly(Application).That().HaveName("AccessQueryService").GetTypes());
    }

    [Fact]
    public void Commerce_types_have_no_dependency_on_the_relationship_store_or_the_engine_client()
    {
        var result = Types.InAssembly(Domain).That().ResideInNamespace("Poc.Domain.Commerce")
            .ShouldNot().HaveDependencyOnAny(typeof(IRelationshipStore).FullName!, "Authzed", "Grpc").GetResult();
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void The_dependency_rule_would_detect_a_violation()
    {
        // Proves the NetArchTest rule can fail: Infrastructure legitimately depends on Authzed, so the same rule fails there.
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOnAny("Authzed").GetResult();
        Assert.False(result.IsSuccessful);
    }
}
