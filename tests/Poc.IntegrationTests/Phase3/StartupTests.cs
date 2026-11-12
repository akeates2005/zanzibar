using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Infrastructure;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>Phase 3 exit criterion: after start-up, reconciliation reports no drift and the matrix passes through the port.</summary>
public sealed class StartupTests : IAsyncLifetime
{
    private static readonly string[] AllTypes =
        [PlatformSchema.Type, GroupSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type];

    private PocHost _host = null!;

    public async Task InitializeAsync() => _host = await PocHost.CreateAsync(runStartup: true);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Seeding_produces_exactly_the_tuples_in_the_PRD_seed_scenario()
    {
        var (expected, _) = ValidationYaml.Load();
        var store = _host.Get<IRelationshipStore>();

        var actual = new List<Relationship>();
        foreach (var type in AllTypes) actual.AddRange(await store.ReadAsync(new RelationshipFilter(type), CancellationToken.None));

        Assert.Equal(expected.Select(r => r.ToString()).Order(), actual.Select(r => r.ToString()).Order());
    }

    [Fact]
    public async Task The_whole_access_matrix_passes_through_the_authorisation_port()
    {
        var (_, assertions) = ValidationYaml.Load();
        Assert.True(assertions.Count >= 70, $"expected the full matrix, found {assertions.Count} assertions");
        var authorizer = _host.Get<IAuthorizer>();

        var wrong = new List<string>();
        foreach (var a in assertions)
        {
            var r = a.Relationship;
            var allowed = await authorizer.CheckAsync(r.Subject, r.Relation, r.Resource, CancellationToken.None);
            if (allowed != a.Expected) wrong.Add($"{r} expected {a.Expected} got {allowed}");
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Reconciliation_reports_no_drift_after_startup()
    {
        var report = await _host.Get<ReconciliationRunner>().RunAsync(repair: false, CancellationToken.None);

        Assert.True(report.InSync, $"missing: {string.Join(", ", report.Missing)}; unexpected: {string.Join(", ", report.Unexpected)}");
        Assert.Equal(0, report.SkippedPending);
    }
}
