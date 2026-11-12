namespace Poc.Application.Authorization;

/// <summary>Writes and reads the authorisation schema. Used by start-up and by the health probe.</summary>
public interface ISchemaStore
{
    Task WriteAsync(string schemaText, CancellationToken ct);

    /// <summary>The stored schema text, or <c>null</c> if the engine holds no schema (for example after a restart).</summary>
    Task<string?> ReadAsync(CancellationToken ct);
}
