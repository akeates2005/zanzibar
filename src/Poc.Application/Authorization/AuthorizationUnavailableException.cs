namespace Poc.Application.Authorization;

/// <summary>
/// The authorisation engine could not give an answer (unreachable, timed out, rejected the credentials or the
/// request). The API maps this to HTTP 503. Callers must treat it as "deny": there is no allow fallback.
/// </summary>
public sealed class AuthorizationUnavailableException : Exception
{
    public AuthorizationUnavailableException(string operation, string detail, Exception? inner = null, bool isTransient = false)
        : base($"Authorisation engine unavailable during {operation}: {detail}", inner)
    {
        Operation = operation;
        IsTransient = isTransient;
    }

    public string Operation { get; }

    /// <summary>
    /// True when retrying later can succeed because the engine was unreachable or too slow. False when the engine
    /// answered and refused (bad key, invalid request), which a retry will not fix. The outbox counts only the
    /// latter towards its five-attempt limit, so an outage longer than ten seconds cannot kill pending messages.
    /// </summary>
    public bool IsTransient { get; }
}
