using Grpc.Core;
using Poc.Application.Authorization;
using Az = Authzed.Api.V1;

namespace Poc.Infrastructure.SpiceDb;

/// <summary><see cref="IRelationshipStore"/> over SpiceDB. Reads are fully consistent.</summary>
public sealed class SpiceDbRelationshipStore(SpiceDbConnection connection) : IRelationshipStore
{
    private const uint DeleteBatchLimit = 1000;

    public async Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        // An empty write has no ZedToken to return; callers must not make one.
        if (updates.Count == 0) throw new ArgumentException("A relationship write needs at least one update.", nameof(updates));

        var request = new Az.WriteRelationshipsRequest();
        foreach (var update in updates) request.Updates.Add(update.ToProto());

        var response = await connection.RunAsync(
            "WriteRelationships",
            deadline => connection.Permissions.WriteRelationshipsAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
            ct).ConfigureAwait(false);
        return TokenOrThrow("WriteRelationships", response.WrittenAt);
    }

    public Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var request = new Az.ReadRelationshipsRequest
        {
            Consistency = SpiceDbConnection.FullyConsistent,
            RelationshipFilter = filter.ToProto(),
        };
        return connection.RunAsync<IReadOnlyList<Relationship>>("ReadRelationships", async deadline =>
        {
            using var call = connection.Permissions.ReadRelationships(request, deadline: deadline, cancellationToken: ct);
            var found = new List<Relationship>();
            await foreach (var item in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
                found.Add(item.Relationship.FromProto());
            return found;
        }, ct);
    }

    public async Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var protoFilter = filter.ToProto();
        while (true)
        {
            var request = new Az.DeleteRelationshipsRequest
            {
                RelationshipFilter = protoFilter,
                OptionalLimit = DeleteBatchLimit,
                OptionalAllowPartialDeletions = true,
            };
            var response = await connection.RunAsync(
                "DeleteRelationships",
                deadline => connection.Permissions.DeleteRelationshipsAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
                ct).ConfigureAwait(false);

            if (response.DeletionProgress == Az.DeleteRelationshipsResponse.Types.DeletionProgress.Complete)
                return TokenOrThrow("DeleteRelationships", response.DeletedAt);
            if (response.DeletionProgress != Az.DeleteRelationshipsResponse.Types.DeletionProgress.Partial
                || response.RelationshipsDeletedCount == 0)
                throw new AuthorizationUnavailableException("DeleteRelationships",
                    $"deletion did not complete (progress {response.DeletionProgress}, deleted {response.RelationshipsDeletedCount})");
        }
    }

    private static string TokenOrThrow(string operation, Az.ZedToken? token) =>
        string.IsNullOrEmpty(token?.Token)
            ? throw new AuthorizationUnavailableException(operation, "the engine returned no ZedToken")
            : token.Token;
}
