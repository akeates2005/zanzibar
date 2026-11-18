using Poc.Application.Authorization;

namespace Poc.UnitTests.Authorization;

/// <summary>
/// The cases observed against SpiceDB v1.57.1 on 2026-11-18 (DECISIONS.md): what the engine accepted as a resource id is
/// accepted here, and what it refused is refused here.
/// </summary>
public sealed class EngineObjectIdTests
{
    [Theory]
    [InlineData("A|b=c+d/e_f-g")]
    [InlineData("UPPER")]
    [InlineData("/lead")]
    [InlineData("-lead")]
    [InlineData("_")]
    [InlineData("=")]
    [InlineData("acme")]
    [InlineData("0191c9d2a3b47000b000123456789abc")]
    public void Accepts_what_the_engine_accepted(string id) => Assert.True(EngineObjectId.IsValid(id));

    [Fact]
    public void Accepts_exactly_the_engines_maximum_length() =>
        Assert.True(EngineObjectId.IsValid(new string('a', EngineObjectId.MaxLength)));

    [Fact]
    public void Refuses_one_character_more_than_the_engine() =>
        Assert.False(EngineObjectId.IsValid(new string('a', EngineObjectId.MaxLength + 1)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]       // the wildcard is a subject, never a resource
    [InlineData("a.b")]
    [InlineData("a:b")]     // would split a tuple's type from its id
    [InlineData("a#b")]     // would inject a relation
    [InlineData("a@b")]     // would inject a subject
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData("a%2Fb")]   // still encoded: the query string is decoded before this rule sees it
    [InlineData("a,b")]     // two query values joined
    [InlineData("acme\n")]  // a trailing newline, which a regex ending in $ would let through
    public void Refuses_what_the_engine_refused(string? id) => Assert.False(EngineObjectId.IsValid(id));
}
