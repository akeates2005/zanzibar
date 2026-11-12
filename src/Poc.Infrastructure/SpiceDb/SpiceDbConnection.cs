using System.Diagnostics;
using Authzed.Api.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Poc.Application.Authorization;

namespace Poc.Infrastructure.SpiceDb;

/// <summary>
/// One gRPC channel to SpiceDB plus the shared call policy: a deadline on every call, a span per call, and
/// translation of every engine failure into <see cref="AuthorizationUnavailableException"/>.
/// </summary>
public sealed class SpiceDbConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public SpiceDbConnection(SpiceDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PresharedKey))
            throw new ArgumentException("A SpiceDB preshared key is required.", nameof(options));
        if (options.Deadline <= TimeSpan.Zero)
            throw new ArgumentException("Deadline must be positive.", nameof(options));
        if (options.LookupPageSize is < 1 or > 1000)
            throw new ArgumentException("LookupPageSize must be between 1 and 1000 (the engine's default cap).", nameof(options));
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException($"Endpoint '{options.Endpoint}' is not an absolute URI.", nameof(options));
        if (endpoint.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureTransport)
            throw new ArgumentException(
                "Refusing to send the preshared key over plain HTTP; use https or set AllowInsecureTransport for local use.",
                nameof(options));

        Deadline = options.Deadline;
        LookupPageSize = options.LookupPageSize;

        var key = options.PresharedKey;
        var credentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            metadata.Add("Authorization", $"Bearer {key}");
            return Task.CompletedTask;
        });
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            Credentials = endpoint.Scheme == Uri.UriSchemeHttps
                ? ChannelCredentials.Create(new SslCredentials(), credentials)
                : ChannelCredentials.Create(ChannelCredentials.Insecure, credentials),
            UnsafeUseInsecureChannelCallCredentials = endpoint.Scheme == Uri.UriSchemeHttp,
        });
        Permissions = new PermissionsService.PermissionsServiceClient(_channel);
        Schema = new SchemaService.SchemaServiceClient(_channel);
    }

    public TimeSpan Deadline { get; }

    public int LookupPageSize { get; }

    internal PermissionsService.PermissionsServiceClient Permissions { get; }

    internal SchemaService.SchemaServiceClient Schema { get; }

    internal static Consistency FullyConsistent => new() { FullyConsistent = true };

    /// <summary>
    /// Runs one engine call. The delegate receives the deadline to put on the gRPC call. Any <see cref="RpcException"/>
    /// becomes <see cref="AuthorizationUnavailableException"/>; cancellation by the caller stays a cancellation.
    /// </summary>
    internal async Task<T> RunAsync<T>(string operation, Func<DateTime, Task<T>> call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var activity = SpiceDbTelemetry.Source.StartActivity($"spicedb.{operation}", ActivityKind.Client);
        activity?.SetTag("rpc.system", "grpc");
        activity?.SetTag("rpc.method", operation);
        try
        {
            return await call(DateTime.UtcNow + Deadline).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }
        catch (RpcException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.StatusCode.ToString());
            throw new AuthorizationUnavailableException(
                operation,
                $"{ex.StatusCode}: {ex.Status.Detail}",
                ex,
                isTransient: ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded);
        }
    }

    public void Dispose() => _channel.Dispose();
}
