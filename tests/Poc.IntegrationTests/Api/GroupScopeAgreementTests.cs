using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Access;
using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// NEXT-STEPS step 4: the group picker and the grant command state the scope rule once and walk the same hierarchy, so they
/// cannot disagree. Checked both ways across every seed object and every group, for a platform administrator (who may
/// grant platform groups) and a customer administrator (who may not).
/// </summary>
public sealed class GroupScopeAgreementTests : ApiTestBase
{
    [Theory]
    [InlineData("priya")]
    [InlineData("alice")]
    public async Task A_group_is_offered_if_and_only_if_granting_it_succeeds(string user)
    {
        var model = Host.Services.GetRequiredService<AccessModelSource>().Current;
        var tuples = Host.Services.GetRequiredService<IRelationshipReader>();
        var objects = new List<ResourceRef> { new("platform", "main") };
        foreach (var type in model.ResourceTypes.Where(t => t != "platform"))
            objects.AddRange((await tuples.ReadAsync(new RelationshipFilter(type, Relation: "parent"), Ct)).Select(t => t.Resource));
        var groups = (await tuples.ReadAsync(new RelationshipFilter("group", Relation: "owner"), Ct)).Select(t => t.Resource.Id).Distinct().ToList();
        Assert.True(objects.Count >= 10, "the seed objects were not found");
        Assert.True(groups.Count >= 4, "the seed groups were not found");

        var client = await AsAsync(user);
        var checkedPairs = 0;
        foreach (var target in objects)
        {
            var q = $"type={Uri.EscapeDataString(target.Type)}&id={target.Id}";
            var picker = await client.GetAsync($"/api/access/grantable-groups?{q}");
            if (picker.StatusCode != HttpStatusCode.OK) continue; // the caller cannot manage access here; nothing to compare
            var offered = (await picker.ExpectAsync(HttpStatusCode.OK)).EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToHashSet();
            var relation = model.GrantableRelations(target.Type)[^1];

            foreach (var group in groups)
            {
                var grant = await client.PutJsonAsync($"/api/access/grants?{q}", new { relation, subject = new { type = "group", id = group } });
                var succeeded = grant.StatusCode == HttpStatusCode.OK;
                Assert.True(offered.Contains(group) == succeeded,
                    $"{user} on {target}: group {group} offered={offered.Contains(group)} but grant returned {(int)grant.StatusCode}");
                if (succeeded)
                    await (await client.DeleteAsync($"/api/access/grants?{q}&relation={relation}&subjectType=group&subjectId={group}")).ExpectAsync(HttpStatusCode.OK);
                checkedPairs++;
            }
        }

        Assert.True(checkedPairs >= 4 * groups.Count, $"only {checkedPairs} object-group pairs were compared");
    }
}
