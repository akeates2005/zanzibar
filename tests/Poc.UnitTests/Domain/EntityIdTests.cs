using Poc.Domain.Common;

namespace Poc.UnitTests.Domain;

public sealed class EntityIdTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("uk-ops")]
    [InlineData("m365-e3")]
    [InlineData("a_b")]
    [InlineData("0191c9d2a3b47000b000123456789abc")]
    public void Accepts_readable_seed_ids_and_generated_ids(string id) => Assert.Equal(id, EntityId.Validate(id));

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]           // upper case
    [InlineData("-leading-dash")]
    [InlineData("a:b")]            // would split a tuple's type from its id
    [InlineData("a#member")]       // would inject a relation
    [InlineData("a@b")]            // would inject a subject
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("*")]              // the engine's wildcard
    [InlineData("a\nb")]
    public void Rejects_anything_that_could_corrupt_a_tuple(string id) =>
        Assert.Throws<DomainRuleException>(() => EntityId.Validate(id));

    [Fact]
    public void Rejects_overlong_ids() =>
        Assert.Throws<DomainRuleException>(() => EntityId.Validate(new string('a', EntityId.MaxLength + 1)));

    [Fact]
    public void Rejects_null() => Assert.Throws<DomainRuleException>(() => EntityId.Validate(null));
}
