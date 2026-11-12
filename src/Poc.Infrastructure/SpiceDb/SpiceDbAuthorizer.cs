using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Az = Authzed.Api.V1;

namespace Poc.Infrastructure.SpiceDb;

/// <summary>
/// <see cref="IAuthorizer"/> over SpiceDB. Fail-closed: only an explicit <c>HasPermission</c> answer is true.
/// Conditional results (caveats) count as denied; the schema defines no caveats, so none are expected.
/// </summary>
public sealed partial class SpiceDbAuthorizer(SpiceDbConnection connection, ILogger<SpiceDbAuthorizer> logger) : IAuthorizer
{
    public async Task<bool> CheckAsync(SubjectRef subject, string permission, ResourceRef resource, CancellationToken ct)
    {
        RequireName(permission);
        var request = new Az.CheckPermissionRequest
        {
            Consistency = SpiceDbConnection.FullyConsistent,
            Resource = resource.ToProto(),
            Subject = subject.ToProto(),
            Permission = permission,
        };
        var started = Stopwatch.GetTimestamp();
        var response = await connection.RunAsync(
            "CheckPermission",
            deadline => connection.Permissions.CheckPermissionAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
            ct).ConfigureAwait(false);
        var allowed = response.Permissionship == Az.CheckPermissionResponse.Types.Permissionship.HasPermission;
        LogCheck(subject.ToString(), permission, resource.ToString(), allowed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return allowed;
    }

    public async Task<IReadOnlyDictionary<string, bool>> CheckManyAsync(
        SubjectRef subject, ResourceRef resource, IReadOnlyCollection<string> permissions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var distinct = permissions.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return new Dictionary<string, bool>();
        foreach (var p in distinct) RequireName(p);

        var request = new Az.CheckBulkPermissionsRequest { Consistency = SpiceDbConnection.FullyConsistent };
        foreach (var p in distinct)
        {
            request.Items.Add(new Az.CheckBulkPermissionsRequestItem
            {
                Resource = resource.ToProto(),
                Subject = subject.ToProto(),
                Permission = p,
            });
        }

        var started = Stopwatch.GetTimestamp();
        var response = await connection.RunAsync(
            "CheckBulkPermissions",
            deadline => connection.Permissions.CheckBulkPermissionsAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
            ct).ConfigureAwait(false);

        if (response.Pairs.Count != distinct.Count)
            throw new AuthorizationUnavailableException("CheckBulkPermissions",
                $"expected {distinct.Count} results but received {response.Pairs.Count}");

        var result = new Dictionary<string, bool>(distinct.Count, StringComparer.Ordinal);
        foreach (var pair in response.Pairs)
        {
            // A per-item error is an engine failure, never an implicit "false".
            if (pair.ResponseCase != Az.CheckBulkPermissionsPair.ResponseOneofCase.Item)
                throw new AuthorizationUnavailableException("CheckBulkPermissions",
                    $"item '{pair.Request?.Permission}' failed: {pair.Error?.Code} {pair.Error?.Message}");
            result[pair.Request.Permission] =
                pair.Item.Permissionship == Az.CheckPermissionResponse.Types.Permissionship.HasPermission;
        }

        if (distinct.Any(p => !result.ContainsKey(p)))
            throw new AuthorizationUnavailableException("CheckBulkPermissions", "response did not cover every requested permission");

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        foreach (var (permission, allowed) in result)
            LogCheck(subject.ToString(), permission, resource.ToString(), allowed, elapsed);
        return result;
    }

    public async Task<IReadOnlyList<string>> LookupResourcesAsync(
        SubjectRef subject, string resourceType, string permission, CancellationToken ct)
    {
        RequireName(resourceType);
        RequireName(permission);
        var subjectProto = subject.ToProto();
        var pageSize = connection.LookupPageSize;
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Az.Cursor? cursor = null;

        while (true)
        {
            var requestCursor = cursor;
            var page = await connection.RunAsync("LookupResources", async deadline =>
            {
                var request = new Az.LookupResourcesRequest
                {
                    Consistency = SpiceDbConnection.FullyConsistent,
                    ResourceObjectType = resourceType,
                    Permission = permission,
                    Subject = subjectProto,
                    OptionalLimit = (uint)pageSize,
                    OptionalCursor = requestCursor,
                };
                using var call = connection.Permissions.LookupResources(request, deadline: deadline, cancellationToken: ct);
                var items = new List<Az.LookupResourcesResponse>();
                await foreach (var item in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false)) items.Add(item);
                return items;
            }, ct).ConfigureAwait(false);

            var added = 0;
            foreach (var item in page)
            {
                if (item.Permissionship != Az.LookupPermissionship.HasPermission) continue;
                if (seen.Add(item.ResourceObjectId))
                {
                    ids.Add(item.ResourceObjectId);
                    added++;
                }
            }

            if (page.Count < pageSize) return ids;
            cursor = page[^1].AfterResultCursor
                ?? throw new AuthorizationUnavailableException("LookupResources", "full page returned without a continuation cursor");
            if (added == 0 && page.Count > 0 && page.All(p => p.Permissionship == Az.LookupPermissionship.HasPermission))
                throw new AuthorizationUnavailableException("LookupResources", "paging made no progress");
        }
    }

    /// <summary>
    /// Reads the engine's single result stream to the end. This is deliberately not paged: SpiceDB 1.57.1 rejects
    /// <c>optional_concrete_limit</c> on LookupSubjects ("concrete limit is not yet supported") and, unlike
    /// LookupResources, applies no 1000-result cap, so the unpaged call is complete (see DECISIONS.md D4).
    /// </summary>
    public async Task<IReadOnlyList<SubjectRef>> LookupSubjectsAsync(
        ResourceRef resource, string permission, string subjectType, CancellationToken ct)
    {
        RequireName(permission);
        RequireName(subjectType);
        var request = new Az.LookupSubjectsRequest
        {
            Consistency = SpiceDbConnection.FullyConsistent,
            Resource = resource.ToProto(),
            Permission = permission,
            SubjectObjectType = subjectType,
        };

        var items = await connection.RunAsync("LookupSubjects", async deadline =>
        {
            using var call = connection.Permissions.LookupSubjects(request, deadline: deadline, cancellationToken: ct);
            var all = new List<Az.LookupSubjectsResponse>();
            await foreach (var item in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false)) all.Add(item);
            return all;
        }, ct).ConfigureAwait(false);

        var subjects = new List<SubjectRef>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var resolved = item.Subject
                ?? throw new AuthorizationUnavailableException("LookupSubjects", "response item carried no resolved subject");
            var id = resolved.SubjectObjectId;
            if (resolved.Permissionship != Az.LookupPermissionship.HasPermission || id == "*") continue;
            if (seen.Add(id)) subjects.Add(new SubjectRef(subjectType, id));
        }

        return subjects;
    }

    /// <summary>Nodes beyond this are dropped from a trace so one pathological schema cannot produce an enormous response.</summary>
    private const int TraceNodeLimit = 500;

    public async Task<CheckExplanation> ExplainAsync(SubjectRef subject, string permission, ResourceRef resource, CancellationToken ct)
    {
        RequireName(permission);
        var request = new Az.CheckPermissionRequest
        {
            Consistency = SpiceDbConnection.FullyConsistent,
            Resource = resource.ToProto(),
            Subject = subject.ToProto(),
            Permission = permission,
            WithTracing = true,
        };
        var started = Stopwatch.GetTimestamp();
        var response = await connection.RunAsync(
            "CheckPermission",
            deadline => connection.Permissions.CheckPermissionAsync(request, deadline: deadline, cancellationToken: ct).ResponseAsync,
            ct).ConfigureAwait(false);
        var allowed = response.Permissionship == Az.CheckPermissionResponse.Types.Permissionship.HasPermission;
        LogCheck(subject.ToString(), permission, resource.ToString(), allowed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var budget = TraceNodeLimit;
        return new CheckExplanation(allowed, response.DebugTrace?.Check is { } root ? MapTrace(root, ref budget) : null);
    }

    private static ExplanationStep MapTrace(Az.CheckDebugTrace trace, ref int budget)
    {
        budget--;
        var children = new List<ExplanationStep>();
        if (trace.SubProblems is { } sub)
        {
            foreach (var child in sub.Traces)
            {
                if (budget <= 0) break;
                children.Add(MapTrace(child, ref budget));
            }
        }

        return new ExplanationStep(
            new ResourceRef(trace.Resource.ObjectType, trace.Resource.ObjectId),
            trace.Permission,
            trace.Subject?.Object is { } s
                ? new SubjectRef(s.ObjectType, s.ObjectId, string.IsNullOrEmpty(trace.Subject.OptionalRelation) ? null : trace.Subject.OptionalRelation)
                : null,
            trace.PermissionType == Az.CheckDebugTrace.Types.PermissionType.Relation,
            trace.Result == Az.CheckDebugTrace.Types.Permissionship.HasPermission,
            children);
    }

    private static void RequireName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Permission and type names must not be empty.");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "authz check subject={Subject} permission={Permission} resource={Resource} allowed={Allowed} durationMs={DurationMs:F1}")]
    private partial void LogCheck(string subject, string permission, string resource, bool allowed, double durationMs);
}
