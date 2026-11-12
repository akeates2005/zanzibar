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
}
