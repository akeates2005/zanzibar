using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Projection;
using Poc.Domain.Commerce;
using Poc.Domain.Common;
using Poc.Domain.Directory;
using Poc.Infrastructure.Persistence;
using Poc.UnitTests.Authorization;

namespace Poc.UnitTests.Projection;

public sealed class EventTupleMapperTests
{
    /// <summary>The model of authz/schema.zed, which the integration tests prove equals what the engine reflects.</summary>
    private static readonly AccessModel Model = AccessModel.FromSchema(SchemaModel.Load().Definitions);

    private static ProjectionPlan Map(IDomainEvent e) => EventTupleMapper.Map(e, Model);

    private static string[] Updates(ProjectionPlan p) => p.Updates.Select(u => $"{u.Operation} {u.Relationship}").ToArray();

    private static string[] Deletes(ProjectionPlan p) =>
        p.Deletes.Select(f => $"{f.ResourceType}:{f.ResourceId ?? "*"}#{f.Relation ?? "*"}@{f.Subject?.SubjectType ?? "*"}:{f.Subject?.SubjectId ?? "*"}#{f.Subject?.SubjectRelation ?? "*"}").ToArray();

    [Fact]
    public void CustomerCreated_touches_the_parent_edge_to_platform_main()
    {
        var plan = Map(new CustomerCreated("acme"));
        Assert.Equal(["Touch customer:acme#parent@platform:main"], Updates(plan));
        Assert.Equal(new ResourceRef("customer", "acme"), plan.Target);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Creation_events_touch_one_parent_edge_each()
    {
        Assert.Equal(["Touch commerce/domain:acme-uk#parent@customer:acme"], Updates(Map(new DomainCreated("acme-uk", "acme"))));
        Assert.Equal(["Touch commerce/wallet:uk-ops#parent@commerce/domain:acme-uk"], Updates(Map(new WalletCreated("uk-ops", "acme-uk"))));
        Assert.Equal(["Touch commerce/subscription:m365#parent@commerce/wallet:uk-ops"], Updates(Map(new SubscriptionCreated("m365", "uk-ops", "carol"))));
    }

    [Fact]
    public void GroupCreated_touches_an_owner_edge_to_the_customer_or_to_platform_main()
    {
        Assert.Equal(["Touch group:uk-it#owner@customer:acme"],
            Updates(Map(new GroupCreated("uk-it", GroupOwnerType.Customer, "acme"))));
        Assert.Equal(["Touch group:support-team#owner@platform:main"],
            Updates(Map(new GroupCreated("support-team", GroupOwnerType.Platform, "main"))));
    }

    [Fact]
    public void WalletMoved_deletes_the_old_parent_and_touches_the_new_one_in_a_single_write()
    {
        var plan = Map(new WalletMoved("uk-rnd", "acme-uk", "acme-ie"));
        Assert.Equal(["Delete commerce/wallet:uk-rnd#parent@commerce/domain:acme-uk", "Touch commerce/wallet:uk-rnd#parent@commerce/domain:acme-ie"], Updates(plan));
        Assert.Empty(plan.Deletes);
    }

    [Theory]
    [MemberData(nameof(BusinessDeletes))]
    public void A_business_object_deletion_removes_every_relationship_where_it_is_the_resource(IDomainEvent e, string type, string id)
    {
        var plan = Map(e);
        Assert.Empty(plan.Updates);
        Assert.Equal([$"{type}:{id}#*@*:*#*"], Deletes(plan));
        Assert.Equal(new ResourceRef(type, id), plan.Target);
    }

    public static TheoryData<IDomainEvent, string, string> BusinessDeletes => new()
    {
        { new CustomerDeleted("acme"), "customer", "acme" },
        { new DomainDeleted("acme-uk"), "commerce/domain", "acme-uk" },
        { new WalletDeleted("uk-ops"), "commerce/wallet", "uk-ops" },
        { new SubscriptionDeleted("m365"), "commerce/subscription", "m365" },
    };

    [Fact]
    public void GroupDeleted_removes_the_group_then_every_place_its_members_were_granted_one_delete_per_resource_type()
    {
        var plan = Map(new GroupDeleted("uk-it"));

        Assert.Empty(plan.Updates);
        Assert.Equal(
        [
            "group:uk-it#*@*:*#*",
            "commerce/domain:*#*@group:uk-it#member",
            "commerce/subscription:*#*@group:uk-it#member",
            "commerce/wallet:*#*@group:uk-it#member",
            "customer:*#*@group:uk-it#member",
            "group:*#*@group:uk-it#member",
            "platform:*#*@group:uk-it#member",
        ], Deletes(plan));
    }

    [Fact]
    public void GroupDeleted_also_removes_grants_on_another_teams_type_that_accepts_groups()
    {
        var withParcel = AccessModel.FromSchema(SchemaModel.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed")) + """
            definition shipping/parcel {
                relation parent: customer
                relation handler: user | group#member
                permission view = handler
                permission manage_access = handler
            }
            """).Definitions);

        Assert.Contains("shipping/parcel:*#*@group:uk-it#member", Deletes(EventTupleMapper.Map(new GroupDeleted("uk-it"), withParcel)));
    }

    [Fact]
    public void GroupDeleted_before_the_model_is_loaded_is_refused_rather_than_leaving_grants_behind()
    {
        Assert.Throws<InvalidOperationException>(() => EventTupleMapper.Map(new GroupDeleted("uk-it"), AccessModel.Empty));
    }

    [Fact]
    public void Every_registered_event_type_has_a_mapping_and_survives_the_outbox_round_trip()
    {
        // Fitness function: adding a domain event without a tuple mapping must fail here, not in production.
        Assert.NotEmpty(OutboxEventSerializer.KnownEventTypes);
        foreach (var type in OutboxEventSerializer.KnownEventTypes)
        {
            var ctor = type.GetConstructors().Single();
            var args = ctor.GetParameters().Select(p => p.ParameterType == typeof(string) ? "x1"
                : p.ParameterType.IsEnum ? Enum.GetValues(p.ParameterType).GetValue(0)! : throw new InvalidOperationException(p.Name)).ToArray();
            var instance = (IDomainEvent)ctor.Invoke(args);

            var plan = Map(instance);
            Assert.False(string.IsNullOrEmpty(plan.Target.Id), type.Name);

            var (name, payload) = OutboxEventSerializer.Serialize(instance);
            Assert.Equal(instance, OutboxEventSerializer.Deserialize(name, payload));
        }
    }

    [Fact]
    public void Every_event_type_in_the_domain_assembly_is_registered_for_the_outbox()
    {
        var all = typeof(IDomainEvent).Assembly.GetTypes().Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
        Assert.Equal(all.Select(t => t.Name).Order(), OutboxEventSerializer.KnownEventTypes.Select(t => t.Name).Order());
    }

    [Fact]
    public void An_unknown_event_is_refused_loudly_never_skipped()
    {
        Assert.Throws<NotSupportedException>(() => Map(new UnknownEvent()));
        Assert.Throws<InvalidOperationException>(() => OutboxEventSerializer.Deserialize("NoSuchEvent", "{}"));
        Assert.Throws<InvalidOperationException>(() => OutboxEventSerializer.Serialize(new UnknownEvent()));
    }

    private sealed record UnknownEvent : IDomainEvent;
}
