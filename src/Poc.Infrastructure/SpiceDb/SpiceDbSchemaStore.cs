using Grpc.Core;
using Poc.Application.Authorization;
using Az = Authzed.Api.V1;

namespace Poc.Infrastructure.SpiceDb;

/// <summary><see cref="ISchemaStore"/> over SpiceDB's schema service.</summary>
public sealed class SpiceDbSchemaStore(SpiceDbConnection connection) : ISchemaStore
{
    public async Task WriteAsync(string schemaText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schemaText)) throw new ArgumentException("Schema text must not be empty.", nameof(schemaText));
        var request = new Az.WriteSchemaRequest { Schema = schemaText };
        await connection.RunAsync(
            "WriteSchema",
            deadline => connection.Schema.WriteSchemaAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
            ct).ConfigureAwait(false);
    }

    public Task<string?> ReadAsync(CancellationToken ct) =>
        connection.RunAsync<string?>("ReadSchema", async deadline =>
        {
            try
            {
                var response = await connection.Schema
                    .ReadSchemaAsync(new Az.ReadSchemaRequest(), deadline: deadline, cancellationToken: ct).ResponseAsync
                    .ConfigureAwait(false);
                return response.SchemaText;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return null;
            }
        }, ct);

    public Task<IReadOnlyList<ReflectedDefinition>> ReflectAsync(CancellationToken ct) =>
        connection.RunAsync<IReadOnlyList<ReflectedDefinition>>("ReflectSchema", async deadline =>
        {
            try
            {
                var response = await connection.Schema
                    .ReflectSchemaAsync(new Az.ReflectSchemaRequest { Consistency = SpiceDbConnection.FullyConsistent }, deadline: deadline, cancellationToken: ct)
                    .ResponseAsync.ConfigureAwait(false);
                return response.Definitions.Select(ToDefinition).ToList();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return [];
            }
        }, ct);

    private static ReflectedDefinition ToDefinition(Az.ReflectionDefinition definition) => new(
        definition.Name,
        definition.Relations.Select(r => new ReflectedRelation(r.Name, r.SubjectTypes.Select(ToAllowedSubject).ToList())).ToList(),
        definition.Permissions.Select(p => p.Name).ToList());

    private static AllowedSubject ToAllowedSubject(Az.ReflectionTypeReference type) => new(
        type.SubjectDefinitionName,
        type.HasOptionalRelationName && type.OptionalRelationName.Length > 0 ? type.OptionalRelationName : null,
        type.HasIsPublicWildcard && type.IsPublicWildcard,
        type.OptionalCaveatName.Length > 0 ? type.OptionalCaveatName : null);
}
