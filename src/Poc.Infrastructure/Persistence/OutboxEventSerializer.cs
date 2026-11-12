using System.Text.Json;
using System.Text.Json.Serialization;
using Poc.Domain.Common;

namespace Poc.Infrastructure.Persistence;

/// <summary>Round-trips domain events through the outbox's <c>Type</c> and <c>Payload</c> columns.</summary>
public static class OutboxEventSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Dictionary<string, Type> EventTypes = typeof(IDomainEvent).Assembly.GetTypes()
        .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<Type> KnownEventTypes => EventTypes.Values;

    public static (string Type, string Payload) Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var type = domainEvent.GetType();
        return EventTypes.ContainsKey(type.Name)
            ? (type.Name, JsonSerializer.Serialize(domainEvent, type, Json))
            : throw new InvalidOperationException($"Domain event {type.Name} is not registered for the outbox.");
    }

    public static IDomainEvent Deserialize(string type, string payload)
    {
        if (!EventTypes.TryGetValue(type, out var eventType))
            throw new InvalidOperationException($"Unknown outbox event type '{type}'.");
        return (IDomainEvent?)JsonSerializer.Deserialize(payload, eventType, Json)
            ?? throw new InvalidOperationException($"Outbox payload for '{type}' deserialised to null.");
    }
}
