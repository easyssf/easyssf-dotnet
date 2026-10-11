using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Revocation;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Revocation;

public class SsfTokenRevocationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T10:00:00Z");

    private const string Issuer = "https://idp.example";

    private readonly TestClock _clock = new(Now);

    private readonly ISsfTokenRevocationStore _store;

    private readonly SsfTokenRevocationEventHandler _handler;

    public SsfTokenRevocationTests()
    {
        _store = new InMemorySsfTokenRevocationStore(TimeSpan.FromMinutes(10), _clock);
        _handler = new SsfTokenRevocationEventHandler(_store, ["CaepSessionRevoked"]);
    }

    [Fact]
    public async Task SessionEventRevokesTokensOfThatSessionOnly()
    {
        await _handler.HandleAsync(SessionRevoked(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        Assert.False(await IsValidAsync(Token("alice", "session-1", Now.AddSeconds(-60))));
        Assert.False(await IsValidAsync(Token("alice", "session-1", Now.AddSeconds(60))));
        Assert.True(await IsValidAsync(Token("alice", "session-2", Now.AddSeconds(-60))));
    }

    [Fact]
    public async Task UserEventRevokesTokensIssuedUpToTheEvent()
    {
        await _handler.HandleAsync(SessionRevoked(IssSub("https://idp.example", "alice")));
        Assert.False(await IsValidAsync(Token("alice", "session-1", Now.AddSeconds(-60))));
        Assert.False(await IsValidAsync(Token("alice", "session-1", Now)));
        Assert.True(await IsValidAsync(Token("alice", "session-2", Now.AddSeconds(1))));
        Assert.True(await IsValidAsync(Token("bob", "session-3", Now.AddSeconds(-60))));
    }

    [Fact]
    public async Task UserEventTypesRevokeTheUserEvenWhenASessionIsNamed()
    {
        SsfTokenRevocationEventHandler handler = new(_store, ["CaepSessionRevoked"], ["CaepCredentialChange"]);
        await handler.HandleAsync(Event(SsfEventTypes.CaepCredentialChange, Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        Assert.False(await IsValidAsync(Token("alice", "session-2", Now.AddSeconds(-60))));
        Assert.False(await _store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.True(await IsValidAsync(Token("alice", "session-2", Now.AddSeconds(1))));
    }

    [Fact]
    public async Task OpaqueSubjectRevokesMatchingSessionAndSubject()
    {
        await _handler.HandleAsync(SessionRevoked(Opaque("abc")));
        Assert.False(await IsValidAsync(Token("alice", "abc", Now.AddSeconds(60))));
        Assert.False(await IsValidAsync(Token("abc", "session-1", Now.AddSeconds(-60))));
        Assert.True(await IsValidAsync(Token("alice", "session-1", Now.AddSeconds(-60))));
    }

    [Fact]
    public async Task RevocationsAreScopedToTheIssuer()
    {
        await _handler.HandleAsync(SessionRevoked(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        await _handler.HandleAsync(SessionRevoked(IssSub("https://idp.example", "bob")));
        Assert.True(await IsValidAsync(new AccessToken("https://other.example", "bob", "session-1", Now.AddSeconds(-60))));
        Assert.False(await _store.IsRevokedAsync("https://other.example", "session-1", null, null));
        Assert.False(await _store.IsRevokedAsync(null, "session-1", "bob", Now));
    }

    [Fact]
    public async Task SessionIsRevokedAtTheIssuerOfItsUser()
    {
        await _handler.HandleAsync(SessionRevoked(Complex(IssSub("https://other.example", "alice"), Opaque("session-1"))));
        Assert.True(await _store.IsSessionRevokedAsync("https://other.example", "session-1"));
        Assert.False(await _store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.True(await IsValidAsync(Token("alice", "session-1", Now)));
    }

    [Fact]
    public async Task IdentifierOfAnotherIssuerIsRevokedAtThatIssuer()
    {
        await _handler.HandleAsync(SessionRevoked(IssSub("https://other.example", "alice")));
        Assert.Equal(Now, await _store.GetSubjectRevokedAtAsync("https://other.example", "alice"));
        Assert.Null(await _store.GetSubjectRevokedAtAsync(Issuer, "alice"));
    }

    [Fact]
    public async Task IgnoresOtherEventsAndSubjectsWithoutSessionOrSubject()
    {
        await _handler.HandleAsync(Event(SsfEventTypes.CaepCredentialChange, IssSub("https://idp.example", "alice")));
        await _handler.HandleAsync(SessionRevoked(Email("alice@example.com")));
        await _handler.HandleAsync(SessionRevoked(null));
        Assert.True(await IsValidAsync(Token("alice", "session-1", Now.AddSeconds(-60))));
    }

    [Fact]
    public async Task RevocationsExpire()
    {
        await _store.RevokeSessionAsync(Issuer, "session-1");
        await _store.RevokeSubjectAsync(Issuer, "alice", Now);
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await _store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.Equal(Now, await _store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await _store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.Null(await _store.GetSubjectRevokedAtAsync(Issuer, "alice"));
    }

    [Fact]
    public async Task EventDeliveredLateDoesNotShortenMoreRecentRevocation()
    {
        await _store.RevokeSubjectAsync(Issuer, "alice", Now);
        await _store.RevokeSubjectAsync(Issuer, "alice", Now.AddSeconds(-120));
        Assert.Equal(Now, await _store.GetSubjectRevokedAtAsync(Issuer, "alice"));
    }

    [Fact]
    public void RejectsNonPositiveTtl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySsfTokenRevocationStore(TimeSpan.Zero));
    }

    private async Task<bool> IsValidAsync(AccessToken token)
    {
        return !await _store.IsRevokedAsync(token.Issuer, token.SessionId, token.Subject, token.IssuedAt);
    }

    private static AccessToken Token(string subject, string sessionId, DateTimeOffset issuedAt)
    {
        return new AccessToken(Issuer, subject, sessionId, issuedAt);
    }

    private sealed record AccessToken(string Issuer, string Subject, string SessionId, DateTimeOffset IssuedAt);

    private static SsfEventContext SessionRevoked(JsonObject? subjectId)
    {
        return Event(SsfEventTypes.CaepSessionRevoked, subjectId);
    }

    private static SsfEventContext Event(string eventType, JsonObject? subjectId)
    {
        List<KeyValuePair<string, JsonElement>> events =
            [new(eventType, SsfJson.ToElement(new JsonObject { ["event_timestamp"] = Now.ToUnixTimeSeconds() }))];
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventContext(new SsfEventToken("jti-1", "https://idp.example", Now, null, events, subject, null, SsfJson.EmptyObject));
    }
}
