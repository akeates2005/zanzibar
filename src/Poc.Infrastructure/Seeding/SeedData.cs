using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Seeding;

/// <summary>
/// The PRD seed scenario: two customers and ten users, chosen so that every inheritance path and every isolation
/// boundary has at least one user who exercises it. The access matrix in authz/validation.yaml is the acceptance test.
/// </summary>
public static class SeedData
{
    public static IReadOnlyList<(string Id, string Name)> Users => SeedUsers.All;

    public static readonly (string Id, string Name)[] Customers = [("acme", "Acme Ltd"), ("globex", "Globex Corporation")];

    public static readonly (string Id, string CustomerId, string Name)[] Domains =
    [
        ("acme-uk", "acme", "Acme UK"), ("acme-ie", "acme", "Acme Ireland"), ("globex-main", "globex", "Globex Main"),
    ];

    public static readonly (string Id, string DomainId, string Name, decimal Balance)[] Wallets =
    [
        ("uk-ops", "acme-uk", "UK Operations", 5000m), ("uk-rnd", "acme-uk", "UK R&D", 2500m),
        ("ie-ops", "acme-ie", "Ireland Operations", 3000m), ("globex-ops", "globex-main", "Globex Operations", 8000m),
    ];

    public static readonly (string Id, string WalletId, string Product, int Quantity)[] Subscriptions =
    [
        ("m365-e3", "uk-ops", "Microsoft 365 E3", 25), ("azure-plan", "uk-ops", "Azure Plan", 1),
        ("github", "uk-rnd", "GitHub Enterprise", 10), ("m365-bp", "ie-ops", "Microsoft 365 Business Premium", 15),
        ("dynamics", "globex-ops", "Dynamics 365 Sales", 5),
    ];

    /// <summary>Groups: the platform's <c>support-team</c> and three Acme groups.</summary>
    public static readonly (string Id, bool Platform, string OwnerId, string Name)[] Groups =
    [
        ("support-team", true, "main", "Support team"), ("uk-it", false, "acme", "UK IT"),
        ("uk-contractors", false, "acme", "UK contractors"), ("finance-ap", false, "acme", "Finance AP"),
    ];

    /// <summary>Group memberships, applied through the group-membership command.</summary>
    public static readonly (string GroupId, SubjectRef Member)[] Memberships =
    [
        ("support-team", SubjectRef.User("sam")),
        ("uk-it", SubjectRef.User("bob")),
        ("uk-contractors", SubjectRef.User("heidi")),
        ("uk-it", SubjectRef.GroupMembers("uk-contractors")),
        ("finance-ap", SubjectRef.User("carol")),
    ];

    /// <summary>Grants, applied through the grant command in this order.</summary>
    public static readonly GrantSeed[] Grants =
    [
        new(PlatformSchema.Main, PlatformSchema.Relations.Administrator, SubjectRef.User("priya")),
        new(PlatformSchema.Main, PlatformSchema.Relations.Support, SubjectRef.GroupMembers("support-team")),
        new(new ResourceRef(CustomerSchema.Type, "acme"), CustomerSchema.Relations.Administrator, SubjectRef.User("alice")),
        new(new ResourceRef(DomainSchema.Type, "acme-uk"), DomainSchema.Relations.Administrator, SubjectRef.GroupMembers("uk-it")),
        new(new ResourceRef(WalletSchema.Type, "uk-ops"), WalletSchema.Relations.Spender, SubjectRef.GroupMembers("finance-ap")),
        new(new ResourceRef(SubscriptionSchema.Type, "m365-e3"), SubscriptionSchema.Relations.Operator, SubjectRef.User("dave")),
        new(new ResourceRef(CustomerSchema.Type, "acme"), CustomerSchema.Relations.Viewer, SubjectRef.User("erin")),
        new(new ResourceRef(CustomerSchema.Type, "globex"), CustomerSchema.Relations.Administrator, SubjectRef.User("grace")),
    ];

    public sealed record GrantSeed(ResourceRef Resource, string Relation, SubjectRef Subject);
}
