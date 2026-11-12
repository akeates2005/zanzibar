namespace Poc.Domain.Directory;

/// <summary>
/// The ten seeded users. Lives in the Domain project, which references nothing, so the application's seeder and the
/// stub token issuer share one list and can never disagree about who exists.
/// </summary>
public static class SeedUsers
{
    public const string EmailDomain = "zanzibar.example";

    public static readonly IReadOnlyList<(string Id, string DisplayName)> All =
    [
        ("priya", "Priya Patel"), ("sam", "Sam Okafor"), ("alice", "Alice Armstrong"), ("bob", "Bob Baker"),
        ("heidi", "Heidi Hughes"), ("carol", "Carol Chen"), ("dave", "Dave Dunn"), ("erin", "Erin Evans"),
        ("grace", "Grace Green"), ("frank", "Frank Foster"),
    ];

    public static string EmailFor(string userId) => $"{userId}@{EmailDomain}";
}
