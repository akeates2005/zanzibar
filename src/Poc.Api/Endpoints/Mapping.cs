using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Application.Queries;
using Poc.Domain.Commerce;

namespace Poc.Api.Endpoints;

/// <summary>Builds object responses: fields, the caller's permission map (one bulk check) and the ancestor labels.</summary>
public sealed class ObjectViews(ObjectReadService read, CurrentUser user)
{
    public async Task<CustomerDto> ToDtoAsync(Customer c, CancellationToken ct)
    {
        var (permissions, ancestors) = await ContextAsync(new ResourceRef("customer", c.Id), ct).ConfigureAwait(false);
        return new CustomerDto(c.Id, c.Name, c.CreatedAt, c.CreatedBy, c.RowVersion, permissions, ancestors);
    }

    public async Task<DomainDto> ToDtoAsync(CustomerDomain d, CancellationToken ct)
    {
        var (permissions, ancestors) = await ContextAsync(new ResourceRef("domain", d.Id), ct).ConfigureAwait(false);
        return new DomainDto(d.Id, d.CustomerId, d.Name, d.CreatedAt, d.CreatedBy, d.RowVersion, permissions, ancestors);
    }

    public async Task<WalletDto> ToDtoAsync(Wallet w, CancellationToken ct)
    {
        var (permissions, ancestors) = await ContextAsync(new ResourceRef("wallet", w.Id), ct).ConfigureAwait(false);
        return new WalletDto(w.Id, w.DomainId, w.Name, w.Balance, w.CreatedAt, w.CreatedBy, w.RowVersion, permissions, ancestors);
    }

    public async Task<SubscriptionDto> ToDtoAsync(Subscription s, CancellationToken ct)
    {
        var (permissions, ancestors) = await ContextAsync(new ResourceRef("subscription", s.Id), ct).ConfigureAwait(false);
        return new SubscriptionDto(s.Id, s.WalletId, s.ProductName, s.Quantity, s.Status, s.CreatedAt, s.CreatedBy, s.RowVersion, permissions, ancestors);
    }

    private async Task<(IReadOnlyDictionary<string, bool>, IReadOnlyList<BreadcrumbDto>)> ContextAsync(ResourceRef resource, CancellationToken ct)
    {
        var permissions = await read.PermissionsAsync(user.Actor, resource, ct).ConfigureAwait(false);
        var crumbs = await read.BreadcrumbsAsync(user.Actor, resource, ct).ConfigureAwait(false);
        return (permissions, crumbs.Select(b => new BreadcrumbDto(b.Type, b.Id, b.Name, b.Viewable)).ToList());
    }
}

internal static class Results2
{
    /// <summary>201 with the object when authorisation landed; 202 with the pending flag when it did not (PRD: How a command runs).</summary>
    public static IResult Created<T>(string location, T dto, string id, bool pending) => pending
        ? Results.Json(new PendingResponse(id, true), statusCode: StatusCodes.Status202Accepted)
        : Results.Created(location, dto);

    /// <summary>200 with the object, or 202 with the pending flag.</summary>
    public static IResult Ok<T>(T dto, string id, bool pending) => pending
        ? Results.Json(new PendingResponse(id, true), statusCode: StatusCodes.Status202Accepted)
        : Results.Ok(dto);

    /// <summary>204 when the change reached the engine; 202 with the pending flag when it did not.</summary>
    public static IResult Deleted(string id, bool pending) => pending
        ? Results.Json(new PendingResponse(id, true), statusCode: StatusCodes.Status202Accepted)
        : Results.NoContent();

    public static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new RuleViolationException("missing_field", $"'{field}' is required.") : value;
}
