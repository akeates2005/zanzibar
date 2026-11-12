namespace Poc.Infrastructure.Persistence;

/// <summary>A domain event waiting to be projected into tuples. Written in the same transaction as its aggregate.</summary>
public sealed class OutboxMessage
{
    public long Sequence { get; set; }

    public string Type { get; set; } = "";

    public string Payload { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
