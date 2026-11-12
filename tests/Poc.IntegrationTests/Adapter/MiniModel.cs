using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

/// <summary>
/// A small, uniquely named slice of the real schema covering each inheritance path, so adapter tests make
/// meaningful assertions without depending on the Phase 3 seed data.
/// </summary>
public sealed class MiniModel
{
    public MiniModel()
    {
        var u = SpiceDbFixture.Unique();
        Platform = new ResourceRef(PlatformSchema.Type, $"p-{u}");
        Customer = new ResourceRef(CustomerSchema.Type, $"c-{u}");
        Domain = new ResourceRef(DomainSchema.Type, $"d-{u}");
        Wallet = new ResourceRef(WalletSchema.Type, $"w-{u}");
        Subscription = new ResourceRef(SubscriptionSchema.Type, $"s-{u}");
        PlatformAdmin = SubjectRef.User($"pa-{u}");
        Support = SubjectRef.User($"sup-{u}");
        CustomerAdmin = SubjectRef.User($"ca-{u}");
        Spender = SubjectRef.User($"sp-{u}");
        Nested = SubjectRef.User($"nested-{u}");
        Stranger = SubjectRef.User($"stranger-{u}");
        SupportGroup = $"sg-{u}";
        SpendGroup = $"g-{u}";
        ContractorGroup = $"ct-{u}";
    }

    public ResourceRef Platform { get; }
    public ResourceRef Customer { get; }
    public ResourceRef Domain { get; }
    public ResourceRef Wallet { get; }
    public ResourceRef Subscription { get; }
    public SubjectRef PlatformAdmin { get; }
    public SubjectRef Support { get; }
    public SubjectRef CustomerAdmin { get; }
    public SubjectRef Spender { get; }
    public SubjectRef Nested { get; }
    public SubjectRef Stranger { get; }
    public string SupportGroup { get; }
    public string SpendGroup { get; }
    public string ContractorGroup { get; }

    public IReadOnlyList<RelationshipUpdate> Tuples() =>
    [
        T(Platform, PlatformSchema.Relations.Administrator, PlatformAdmin),
        T(Platform, PlatformSchema.Relations.Support, SubjectRef.GroupMembers(SupportGroup)),
        T(new(GroupSchema.Type, SupportGroup), GroupSchema.Relations.Owner, Platform),
        T(new(GroupSchema.Type, SupportGroup), GroupSchema.Relations.Member, Support),
        T(Customer, CustomerSchema.Relations.Parent, Platform),
        T(Customer, CustomerSchema.Relations.Administrator, CustomerAdmin),
        T(Domain, DomainSchema.Relations.Parent, Customer),
        T(Wallet, WalletSchema.Relations.Parent, Domain),
        T(Subscription, SubscriptionSchema.Relations.Parent, Wallet),
        T(new(GroupSchema.Type, SpendGroup), GroupSchema.Relations.Owner, Customer),
        T(new(GroupSchema.Type, SpendGroup), GroupSchema.Relations.Member, Spender),
        T(new(GroupSchema.Type, ContractorGroup), GroupSchema.Relations.Owner, Customer),
        T(new(GroupSchema.Type, ContractorGroup), GroupSchema.Relations.Member, Nested),
        T(new(GroupSchema.Type, SpendGroup), GroupSchema.Relations.Member, SubjectRef.GroupMembers(ContractorGroup)),
        T(Wallet, WalletSchema.Relations.Spender, SubjectRef.GroupMembers(SpendGroup)),
    ];

    public async Task WriteAsync(IRelationshipStore store) => await store.WriteAsync(Tuples(), CancellationToken.None);

    private static RelationshipUpdate T(ResourceRef resource, string relation, ResourceRef subject) =>
        RelationshipUpdate.Touch(new Relationship(resource, relation, new SubjectRef(subject.Type, subject.Id)));

    private static RelationshipUpdate T(ResourceRef resource, string relation, SubjectRef subject) =>
        RelationshipUpdate.Touch(new Relationship(resource, relation, subject));
}
