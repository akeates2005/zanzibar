using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Commerce;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Commerce;

namespace Poc.Api.Endpoints;

/// <summary>Customers, domains, wallets and subscriptions. Each route's required permission is declared, not coded.</summary>
public static class ObjectEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- Customers -------------------------------------------------------------------------------------------
        api.MapPost("/customers", async (NameRequest body, CommerceService commerce, ObjectViews views, CurrentUser user, CancellationToken ct) =>
        {
            var result = await commerce.CreateCustomerAsync(user.Actor, Results2.Require(body.Name, "name"), ct);
            return Results2.Created($"/api/customers/{result.Value.Id}", result.AuthorisationPending ? null! : await views.ToDtoAsync(result.Value, ct), result.Value.Id, result.AuthorisationPending);
        }).WithName("CreateCustomer").WithTags("Customers")
          .RequiresOn(PlatformSchema.Permissions.CreateCustomer, ResourceLocator.Platform)
          .Produces<CustomerDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/customers/{id}", async (string id, IRepository<Customer> repo, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await repo.FindAsync(id, ct) ?? throw new NotFoundException($"customer:{id}"), ct)))
          .WithName("GetCustomer").WithTags("Customers").RequiresOn(CustomerSchema.Permissions.View, ResourceLocator.Route(CustomerSchema.Type))
          .Produces<CustomerDto>();

        api.MapPatch("/customers/{id}", async (string id, NameRequest body, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.RenameCustomerAsync(id, Results2.Require(body.Name, "name"), ct), ct)))
          .WithName("RenameCustomer").WithTags("Customers").RequiresOn(CustomerSchema.Permissions.Edit, ResourceLocator.Route(CustomerSchema.Type))
          .Produces<CustomerDto>();

        api.MapDelete("/customers/{id}", async (string id, CommerceService commerce, CancellationToken ct) =>
            Results2.Deleted(id, await commerce.DeleteCustomerAsync(id, ct)))
          .WithName("DeleteCustomer").WithTags("Customers").RequiresOn(CustomerSchema.Permissions.Delete, ResourceLocator.Route(CustomerSchema.Type))
          .Produces(StatusCodes.Status204NoContent).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/customers/{id}/domains", async (string id, NameRequest body, CommerceService commerce, ObjectViews views, CurrentUser user, CancellationToken ct) =>
        {
            var result = await commerce.CreateDomainAsync(user.Actor, id, Results2.Require(body.Name, "name"), ct);
            return Results2.Created($"/api/domains/{result.Value.Id}", result.AuthorisationPending ? null! : await views.ToDtoAsync(result.Value, ct), result.Value.Id, result.AuthorisationPending);
        }).WithName("CreateDomain").WithTags("Customers")
          .RequiresOn(CustomerSchema.Permissions.CreateDomain, ResourceLocator.Route(CustomerSchema.Type))
          .Produces<DomainDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        // ---- Domains ---------------------------------------------------------------------------------------------
        api.MapGet("/domains/{id}", async (string id, IRepository<CustomerDomain> repo, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await repo.FindAsync(id, ct) ?? throw new NotFoundException($"domain:{id}"), ct)))
          .WithName("GetDomain").WithTags("Domains").RequiresOn(DomainSchema.Permissions.View, ResourceLocator.Route(DomainSchema.Type))
          .Produces<DomainDto>();

        api.MapPatch("/domains/{id}", async (string id, NameRequest body, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.RenameDomainAsync(id, Results2.Require(body.Name, "name"), ct), ct)))
          .WithName("RenameDomain").WithTags("Domains").RequiresOn(DomainSchema.Permissions.Edit, ResourceLocator.Route(DomainSchema.Type))
          .Produces<DomainDto>();

        api.MapDelete("/domains/{id}", async (string id, CommerceService commerce, CancellationToken ct) =>
            Results2.Deleted(id, await commerce.DeleteDomainAsync(id, ct)))
          .WithName("DeleteDomain").WithTags("Domains").RequiresOn(DomainSchema.Permissions.Delete, ResourceLocator.Route(DomainSchema.Type))
          .Produces(StatusCodes.Status204NoContent).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/domains/{id}/wallets", async (string id, NameRequest body, CommerceService commerce, ObjectViews views, CurrentUser user, CancellationToken ct) =>
        {
            var result = await commerce.CreateWalletAsync(user.Actor, id, Results2.Require(body.Name, "name"), ct);
            return Results2.Created($"/api/wallets/{result.Value.Id}", result.AuthorisationPending ? null! : await views.ToDtoAsync(result.Value, ct), result.Value.Id, result.AuthorisationPending);
        }).WithName("CreateWallet").WithTags("Domains")
          .RequiresOn(DomainSchema.Permissions.CreateWallet, ResourceLocator.Route(DomainSchema.Type))
          .Produces<WalletDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        // ---- Wallets ---------------------------------------------------------------------------------------------
        api.MapGet("/wallets/{id}", async (string id, IRepository<Wallet> repo, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await repo.FindAsync(id, ct) ?? throw new NotFoundException($"wallet:{id}"), ct)))
          .WithName("GetWallet").WithTags("Wallets").RequiresOn(WalletSchema.Permissions.View, ResourceLocator.Route(WalletSchema.Type))
          .Produces<WalletDto>();

        api.MapPatch("/wallets/{id}", async (string id, NameRequest body, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.RenameWalletAsync(id, Results2.Require(body.Name, "name"), ct), ct)))
          .WithName("RenameWallet").WithTags("Wallets").RequiresOn(WalletSchema.Permissions.Edit, ResourceLocator.Route(WalletSchema.Type))
          .Produces<WalletDto>();

        api.MapDelete("/wallets/{id}", async (string id, CommerceService commerce, CancellationToken ct) =>
            Results2.Deleted(id, await commerce.DeleteWalletAsync(id, ct)))
          .WithName("DeleteWallet").WithTags("Wallets").RequiresOn(WalletSchema.Permissions.Delete, ResourceLocator.Route(WalletSchema.Type))
          .Produces(StatusCodes.Status204NoContent).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/wallets/{id}/top-up", async (string id, TopUpRequest body, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.TopUpWalletAsync(id, body.Amount, ct), ct)))
          .WithName("TopUpWallet").WithTags("Wallets").RequiresOn(WalletSchema.Permissions.TopUp, ResourceLocator.Route(WalletSchema.Type))
          .Produces<WalletDto>();

        // The move needs `move` on the wallet (declared and enforced by the filter) and `create_wallet` on the target
        // domain, which is named in the body, so the handler checks that one through the same ResourceAccess rule.
        api.MapPost("/wallets/{id}/move", async (string id, MoveWalletRequest body, CommerceService commerce, ResourceAccess access, ObjectViews views, CurrentUser user, CancellationToken ct) =>
        {
            var target = Results2.Require(body.TargetDomainId, "targetDomainId");
            await access.RequireAsync(user.Actor, DomainSchema.Permissions.CreateWallet, new ResourceRef(DomainSchema.Type, target), ct);
            var result = await commerce.MoveWalletAsync(id, target, ct);
            return Results2.Ok(result.AuthorisationPending ? null! : await views.ToDtoAsync(result.Value, ct), id, result.AuthorisationPending);
        }).WithName("MoveWallet").WithTags("Wallets")
          .RequiresOn(WalletSchema.Permissions.Move, ResourceLocator.Route(WalletSchema.Type))
          .Produces<WalletDto>().Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/wallets/{id}/subscriptions", async (string id, CreateSubscriptionRequest body, CommerceService commerce, ObjectViews views, CurrentUser user, CancellationToken ct) =>
        {
            var result = await commerce.CreateSubscriptionAsync(user.Actor, id, Results2.Require(body.ProductName, "productName"), body.Quantity, ct);
            return Results2.Created($"/api/subscriptions/{result.Value.Id}", result.AuthorisationPending ? null! : await views.ToDtoAsync(result.Value, ct), result.Value.Id, result.AuthorisationPending);
        }).WithName("CreateSubscription").WithTags("Wallets")
          .RequiresOn(WalletSchema.Permissions.CreateSubscription, ResourceLocator.Route(WalletSchema.Type))
          .Produces<SubscriptionDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        // ---- Subscriptions ---------------------------------------------------------------------------------------
        api.MapGet("/subscriptions/{id}", async (string id, IRepository<Subscription> repo, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await repo.FindAsync(id, ct) ?? throw new NotFoundException($"subscription:{id}"), ct)))
          .WithName("GetSubscription").WithTags("Subscriptions").RequiresOn(SubscriptionSchema.Permissions.View, ResourceLocator.Route(SubscriptionSchema.Type))
          .Produces<SubscriptionDto>();

        api.MapDelete("/subscriptions/{id}", async (string id, CommerceService commerce, CancellationToken ct) =>
            Results2.Deleted(id, await commerce.DeleteSubscriptionAsync(id, ct)))
          .WithName("DeleteSubscription").WithTags("Subscriptions").RequiresOn(SubscriptionSchema.Permissions.Delete, ResourceLocator.Route(SubscriptionSchema.Type))
          .Produces(StatusCodes.Status204NoContent).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/subscriptions/{id}/quantity", async (string id, QuantityRequest body, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.ChangeSubscriptionQuantityAsync(id, body.Quantity, ct), ct)))
          .WithName("ChangeSubscriptionQuantity").WithTags("Subscriptions")
          .RequiresOn(SubscriptionSchema.Permissions.ChangeQuantity, ResourceLocator.Route(SubscriptionSchema.Type))
          .Produces<SubscriptionDto>();

        api.MapPost("/subscriptions/{id}/cancel", async (string id, CommerceService commerce, ObjectViews views, CancellationToken ct) =>
            Results.Ok(await views.ToDtoAsync(await commerce.CancelSubscriptionAsync(id, ct), ct)))
          .WithName("CancelSubscription").WithTags("Subscriptions")
          .RequiresOn(SubscriptionSchema.Permissions.Cancel, ResourceLocator.Route(SubscriptionSchema.Type))
          .Produces<SubscriptionDto>();
    }
}
