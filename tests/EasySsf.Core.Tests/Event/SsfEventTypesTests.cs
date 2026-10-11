using EasySsf.Core.Event;

namespace EasySsf.Core.Tests.Event;

public class SsfEventTypesTests
{
    private const string AcmeLogin = "https://events.acme.example/login";

    [Fact]
    public void ResolvesBuiltInAliasesAndPassesUrisThrough()
    {
        Assert.Equal(SsfEventTypes.CaepSessionRevoked, SsfEventTypes.Resolve("CaepSessionRevoked"));
        Assert.Equal(AcmeLogin, SsfEventTypes.Resolve(AcmeLogin));
        Assert.Equal("CaepSessionRevoked", SsfEventTypes.AliasOf(SsfEventTypes.CaepSessionRevoked));
        Assert.Equal(AcmeLogin, SsfEventTypes.AliasOf(AcmeLogin));
        Assert.True(SsfEventTypes.IsAlias("CaepSessionRevoked"));
        Assert.False(SsfEventTypes.IsAlias(AcmeLogin));
        Assert.False(SsfEventTypes.IsAlias(null));
    }

    [Fact]
    public void ScimEventTypesAreUrnsWithAliases()
    {
        Assert.Equal("urn:ietf:params:scim:event:prov:create:full", SsfEventTypes.Resolve("ScimProvCreateFull"));
        Assert.Equal("ScimProvDeactivate", SsfEventTypes.AliasOf(SsfEventTypes.ScimProvDeactivate));
        Assert.Equal("ScimMiscAsyncResponse", SsfEventTypes.AliasOf(SsfEventTypes.ScimMiscAsyncResponse));
        Assert.Equal(SsfEventTypes.ScimFeedRemove, SsfEventTypes.Aliases()["ScimFeedRemove"]);
    }

    [Fact]
    public void KnowsTheCaepAndRiscNamespaces()
    {
        Assert.True(SsfEventTypes.IsCaepEvent("CaepSessionRevoked"));
        Assert.True(SsfEventTypes.IsCaepEvent("https://schemas.openid.net/secevent/caep/event-type/future-event"));
        Assert.False(SsfEventTypes.IsCaepEvent(SsfEventTypes.RiscAccountDisabled));
        Assert.True(SsfEventTypes.IsRiscEvent("RiscSessionsRevoked"));
        Assert.False(SsfEventTypes.IsRiscEvent("ScimProvDelete"));
        Assert.False(SsfEventTypes.IsRiscEvent(null));
    }

    [Fact]
    public void RegisteredAliasResolvesLikeABuiltInOne()
    {
        SsfEventTypes.RegisterAlias("AcmeLogin", AcmeLogin);
        Assert.Equal(AcmeLogin, SsfEventTypes.Resolve("AcmeLogin"));
        Assert.Equal("AcmeLogin", SsfEventTypes.AliasOf(AcmeLogin));
        IReadOnlyDictionary<string, string> aliases = SsfEventTypes.Aliases();
        Assert.Equal(AcmeLogin, aliases["AcmeLogin"]);
        Assert.Equal(SsfEventTypes.CaepSessionRevoked, aliases["CaepSessionRevoked"]);
        // registering the same mapping again is fine
        SsfEventTypes.RegisterAlias("AcmeLogin", AcmeLogin);
    }

    [Fact]
    public void AliasCannotBeMappedToAnotherUri()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SsfEventTypes.RegisterAlias("CaepSessionRevoked", AcmeLogin));
        Assert.Contains("already mapped", ex.Message);
        SsfEventTypes.RegisterAlias("AcmeLogout", "https://events.acme.example/logout");
        ex = Assert.Throws<ArgumentException>(
            () => SsfEventTypes.RegisterAlias("AcmeLogout", "https://events.acme.example/other"));
        Assert.Contains("already mapped", ex.Message);
        Assert.Equal(SsfEventTypes.CaepSessionRevoked, SsfEventTypes.Resolve("CaepSessionRevoked"));
    }

    [Fact]
    public void UriMayHaveSeveralAliasesAndKeepsTheFirstForDisplay()
    {
        SsfEventTypes.RegisterAlias("SessionRevoked", SsfEventTypes.CaepSessionRevoked);
        Assert.Equal(SsfEventTypes.CaepSessionRevoked, SsfEventTypes.Resolve("SessionRevoked"));
        Assert.Equal("CaepSessionRevoked", SsfEventTypes.AliasOf(SsfEventTypes.CaepSessionRevoked));
    }

    [Theory]
    [InlineData("", AcmeLogin)]
    [InlineData("urn:x", AcmeLogin)]
    [InlineData("a/b", AcmeLogin)]
    [InlineData(" Acme", AcmeLogin)]
    [InlineData("Acme", "login")]
    [InlineData("Acme", null)]
    public void RejectsMalformedAliasesAndUris(string alias, string? uri)
    {
        Assert.Throws<ArgumentException>(() => SsfEventTypes.RegisterAlias(alias, uri!));
    }
}
