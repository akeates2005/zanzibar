namespace Poc.Application.Authorization;

/// <summary>One node of the engine's check trace: why a permission on a resource did or did not resolve.</summary>
public sealed record ExplanationStep(
    ResourceRef Resource,
    string Permission,
    SubjectRef? Subject,
    bool IsRelation,
    bool Allowed,
    IReadOnlyList<ExplanationStep> Children);

/// <summary>The answer and, where the pinned SpiceDB version returns one, the trace behind it.</summary>
public sealed record CheckExplanation(bool Allowed, ExplanationStep? Trace);

/// <summary>
/// Asks the engine questions. Application code checks permissions only, never relations.
/// Every method is fully consistent and throws <see cref="AuthorizationUnavailableException"/> on any engine failure.
/// </summary>
public interface IAuthorizer
{
    Task<bool> CheckAsync(SubjectRef subject, string permission, ResourceRef resource, CancellationToken ct);

    /// <summary>One bulk round trip. The result has an entry for every distinct permission requested.</summary>
    Task<IReadOnlyDictionary<string, bool>> CheckManyAsync(
        SubjectRef subject, ResourceRef resource, IReadOnlyCollection<string> permissions, CancellationToken ct);

    /// <summary>Ids of every resource of <paramref name="resourceType"/> the subject holds the permission on.</summary>
    Task<IReadOnlyList<string>> LookupResourcesAsync(
        SubjectRef subject, string resourceType, string permission, CancellationToken ct);

    /// <summary>Every subject of <paramref name="subjectType"/> that holds the permission on the resource.</summary>
    Task<IReadOnlyList<SubjectRef>> LookupSubjectsAsync(
        ResourceRef resource, string permission, string subjectType, CancellationToken ct);

    /// <summary>A single check with the engine's resolution trace, for the permission explorer.</summary>
    Task<CheckExplanation> ExplainAsync(SubjectRef subject, string permission, ResourceRef resource, CancellationToken ct);
}
