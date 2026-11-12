namespace Poc.Application.Authorization;

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
}
