namespace Poc.Domain.Common;

/// <summary>An aggregate invariant was violated. The API maps this to HTTP 422.</summary>
public sealed class DomainRuleException(string message) : Exception(message);
