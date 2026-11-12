using Poc.Application.Projection;
using Poc.Application.Queries;
using Poc.Domain.Commerce;

namespace Poc.Api.Contracts;

// ---- Requests ---------------------------------------------------------------------------------------------------

public sealed record NameRequest(string? Name);

public sealed record TopUpRequest(decimal Amount);

public sealed record MoveWalletRequest(string? TargetDomainId);

public sealed record CreateSubscriptionRequest(string? ProductName, int Quantity);

public sealed record QuantityRequest(int Quantity);

public sealed record SubjectRequest(string? Type, string? Id);

public sealed record GrantRequest(string? Relation, SubjectRequest? Subject);

public sealed record ExplainRequest(string? UserId, string? Permission, string? ResourceType, string? ResourceId);

// ---- Responses --------------------------------------------------------------------------------------------------

/// <param name="PlatformPermissions">The caller's permissions on the platform, so the shell can decide what to offer (System, new customer).</param>
public sealed record MeResponse(string UserId, string? Name, IReadOnlyList<string> Emails, IReadOnlyDictionary<string, bool> PlatformPermissions);

public sealed record GroupDetailDto(
    GroupSummary Group, IReadOnlyList<GroupMemberView> Members, IReadOnlyList<GroupGrantView> GrantedOn, IReadOnlyDictionary<string, bool> Permissions);

/// <summary>Returned with 202 when a change is saved but the engine could not be told yet.</summary>
public sealed record PendingResponse(string Id, bool AuthorisationPending);

public sealed record BreadcrumbDto(string Type, string? Id, string Name, bool Viewable);

public sealed record CustomerDto(
    string Id, string Name, DateTimeOffset CreatedAt, string CreatedBy, int RowVersion,
    IReadOnlyDictionary<string, bool> Permissions, IReadOnlyList<BreadcrumbDto> Ancestors);

public sealed record DomainDto(
    string Id, string CustomerId, string Name, DateTimeOffset CreatedAt, string CreatedBy, int RowVersion,
    IReadOnlyDictionary<string, bool> Permissions, IReadOnlyList<BreadcrumbDto> Ancestors);

public sealed record WalletDto(
    string Id, string DomainId, string Name, decimal Balance, DateTimeOffset CreatedAt, string CreatedBy, int RowVersion,
    IReadOnlyDictionary<string, bool> Permissions, IReadOnlyList<BreadcrumbDto> Ancestors);

public sealed record SubscriptionDto(
    string Id, string WalletId, string ProductName, int Quantity, SubscriptionStatus Status, DateTimeOffset CreatedAt, string CreatedBy, int RowVersion,
    IReadOnlyDictionary<string, bool> Permissions, IReadOnlyList<BreadcrumbDto> Ancestors);

public sealed record ObjectSummaryDto(string Type, string Id, string Name);

public sealed record TreeNodeDto(string Type, string Id, string Name, IReadOnlyList<BreadcrumbDto> Ancestors);

public sealed record GroupCreatedDto(string Id, string Name, bool AuthorisationPending);

/// <summary>One step of the engine's check trace. The trace is a flat pre-order list; <paramref name="Depth"/> says how deeply a step is nested.</summary>
public sealed record ExplanationStepDto(
    int Depth, string ResourceType, string ResourceId, string Permission, string? SubjectType, string? SubjectId, string? SubjectRelation,
    bool IsRelation, bool Allowed);

/// <param name="Trace">Empty when the engine returned no trace.</param>
public sealed record ExplainResponse(bool Allowed, IReadOnlyList<ExplanationStepDto> Trace);

public sealed record GrantResponse(long AuditId);

public sealed record ReconciliationTupleDto(string Tuple);

public sealed record ReconciliationDto(
    DateTimeOffset At, bool InSync, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected, int SkippedPending);

public sealed record OutboxDto(OutboxStatus Outbox, ReconciliationDto? Reconciliation, bool Ready, int StartupCount);

public sealed record HealthResponse(string Status, bool Ready, string Engine, int OutboxPending, int OutboxFailed, double? OutboxLagSeconds);
