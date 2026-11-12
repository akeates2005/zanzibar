using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Domain.Commerce;

namespace Poc.Application.Projection;

/// <summary>
/// A wallet spender who is not an admin can create a subscription but, without a grant, could not see it. When
/// <see cref="SubscriptionCreated"/> is processed and the creator holds no access to the new subscription, a normal
/// grant command makes the creator its <c>operator</c>, through the audit like any other. No other creation needs one,
/// because every other create permission already requires admin.
/// </summary>
public sealed class DefaultGrantPolicy(IAuthorizer authorizer, AccessService access)
{
    public async Task ApplyAsync(SubscriptionCreated created, CancellationToken ct)
    {
        // Seed data and other system-made subscriptions have no real creator to grant to.
        if (created.CreatedBy == Actor.SystemUserId) return;

        var subscription = new ResourceRef(SubscriptionSchema.Type, created.SubscriptionId);
        var creator = SubjectRef.User(created.CreatedBy);

        // "Lacks admin" is the PRD's condition. Also testing change_quantity makes a replayed message a no-op once the
        // operator grant exists, instead of writing a second audit pair for the same grant.
        var held = await authorizer.CheckManyAsync(
            creator, subscription,
            [SubscriptionSchema.Permissions.Admin, SubscriptionSchema.Permissions.ChangeQuantity], ct).ConfigureAwait(false);
        if (held.Values.Any(v => v)) return;

        await access.GrantAsync(
            Actor.System,
            new GrantTarget(subscription, SubscriptionSchema.Relations.Operator, creator),
            ct).ConfigureAwait(false);
    }
}
