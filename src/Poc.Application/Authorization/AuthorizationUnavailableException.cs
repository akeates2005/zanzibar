namespace Poc.Application.Authorization;

/// <summary>
/// The authorisation engine could not give an answer (unreachable, timed out, rejected the credentials or the
/// request). The API maps this to HTTP 503. Callers must treat it as "deny": there is no allow fallback.
/// </summary>
public sealed class AuthorizationUnavailableException : Exception
{
    public AuthorizationUnavailableException(string operation, string detail, Exception? inner = null)
        : base($"Authorisation engine unavailable during {operation}: {detail}", inner)
    {
        Operation = operation;
    }

    public string Operation { get; }
}
