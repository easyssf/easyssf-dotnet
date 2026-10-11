using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Set;

public sealed class JwtSsfSetVerifierTests : IClassFixture<TestTransmitter>, IDisposable
{
    private readonly TestTransmitter _transmitter;

    private readonly HttpClient _http;

    private JwtSsfSetVerifier _verifier;

    public JwtSsfSetVerifierTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _transmitter.PublishJwks(_transmitter.Key);
        _http = _transmitter.CreateHttpClient();
        _verifier = Verifier(_transmitter.JwksUri);
    }

    public void Dispose()
    {
        _verifier.Dispose();
        _http.Dispose();
    }

    private JwtSsfSetVerifier Verifier(string jwksUri, Action<SsfSetVerifierOptions>? configure = null)
    {
        SsfSetVerifierOptions options = new();
        options.ExpectedAudiences.Add(TestTransmitter.Audience);
        configure?.Invoke(options);
        return new JwtSsfSetVerifier(_transmitter.Issuer, new Uri(jwksUri), _http, options);
    }

    [Fact]
    public async Task RejectsMalformedSet()
    {
        await AssertRejectedAsync("not-a-jwt", SsfSetVerificationException.InvalidRequest);
        await AssertRejectedAsync("", SsfSetVerificationException.InvalidRequest);
        await AssertRejectedAsync("a.b.c", SsfSetVerificationException.InvalidRequest);
        await AssertRejectedAsync("e30.e30.e30", SsfSetVerificationException.InvalidRequest);
    }

    [Fact]
    public async Task RejectsSetSignedWithUnknownKey()
    {
        using TestSigningKey otherKey = TestSigningKey.Generate(2048, _transmitter.Key.KeyId);
        await AssertRejectedAsync(TestTransmitter.Sign(otherKey, TestTransmitter.SecEventJwtType, SessionRevoked()),
            SsfSetVerificationException.InvalidKey);
    }

    [Fact]
    public async Task RejectsUnsignedSet()
    {
        await AssertRejectedAsync(TestTransmitter.Unsigned(SessionRevoked()), SsfSetVerificationException.InvalidRequest);
    }

    [Fact]
    public async Task RejectsAlgorithmThatIsNotAccepted()
    {
        string set = TestTransmitter.Sign(_transmitter.Key, "RS512", TestTransmitter.SecEventJwtType, SessionRevoked());
        await AssertRejectedAsync(set, SsfSetVerificationException.InvalidKey);
        _verifier = Verifier(_transmitter.JwksUri, options => options.AcceptedAlgorithms.Add("RS512"));
        Assert.NotNull(await _verifier.VerifyAsync(set));
    }

    [Fact]
    public void RejectsAlgorithmsThatAreNotAsymmetricSignatures()
    {
        Assert.Throws<ArgumentException>(() => Verifier(_transmitter.JwksUri, options => options.AcceptedAlgorithms.Add("HS256")));
        Assert.Throws<ArgumentException>(() => Verifier(_transmitter.JwksUri, options => options.AcceptedAlgorithms.Add("none")));
        Assert.Throws<ArgumentException>(() => Verifier(_transmitter.JwksUri, options => options.AcceptedAlgorithms.Clear()));
    }

    [Fact]
    public async Task RejectsSigningKeyBelowMinimumSize()
    {
        using TestSigningKey weakKey = TestSigningKey.Generate(1024, "weak-key");
        _transmitter.PublishJwks(weakKey);
        string set = TestTransmitter.Sign(weakKey, TestTransmitter.SecEventJwtType, SessionRevoked());
        await AssertRejectedAsync(set, SsfSetVerificationException.InvalidKey);
        _verifier = Verifier(_transmitter.JwksUri, options => options.MinRsaKeySize = 0);
        Assert.NotNull(await _verifier.VerifyAsync(set));
    }

    [Fact]
    public async Task RejectsSetThatIsNotTypedAsSecurityEvent()
    {
        string set = TestTransmitter.Sign(_transmitter.Key, TestTransmitter.JwtType, SessionRevoked());
        await AssertRejectedAsync(set, SsfSetVerificationException.InvalidRequest);
        string untyped = TestTransmitter.Sign(_transmitter.Key, "RS256", null, SessionRevoked());
        await AssertRejectedAsync(untyped, SsfSetVerificationException.InvalidRequest);
        _verifier = Verifier(_transmitter.JwksUri, options => options.RequireTypeHeader = false);
        Assert.NotNull(await _verifier.VerifyAsync(set));
        Assert.NotNull(await _verifier.VerifyAsync(untyped));
    }

    [Fact]
    public async Task RejectsAccessTokenPresentedAsSet()
    {
        await AssertRejectedAsync(_transmitter.AccessToken("alice", "session-1", DateTimeOffset.UtcNow),
            SsfSetVerificationException.InvalidRequest);
    }

    [Fact]
    public async Task RejectsSetOfAnotherIssuer()
    {
        JsonObject claims = SessionRevoked();
        claims["iss"] = "https://other.example";
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidIssuer);
        claims.Remove("iss");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidIssuer);
    }

    [Fact]
    public async Task RejectsSetForAnotherAudience()
    {
        JsonObject claims = SessionRevoked();
        claims["aud"] = "https://other.example";
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidAudience);
        claims["aud"] = new JsonArray("https://other.example", "https://third.example");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidAudience);
        claims.Remove("aud");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidAudience);
        claims["aud"] = 42;
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
    }

    [Fact]
    public async Task AcceptsAnyAudienceWhenNoneIsExpected()
    {
        _verifier.Options.ExpectedAudiences.Clear();
        JsonObject claims = SessionRevoked();
        claims["aud"] = "https://other.example";
        Assert.NotNull(await _verifier.VerifyAsync(_transmitter.SignSet(claims)));
    }

    [Fact]
    public async Task AcceptsSetAddressedToOneOfSeveralAudiences()
    {
        JsonObject claims = SessionRevoked();
        claims["aud"] = new JsonArray("https://other.example", TestTransmitter.Audience);
        SsfEventToken token = await _verifier.VerifyAsync(_transmitter.SignSet(claims));
        Assert.Equal(["https://other.example", TestTransmitter.Audience], token.Aud);
    }

    [Fact]
    public async Task ConsultsTheAudienceProviderForEverySet()
    {
        List<string> audiences = [];
        _verifier = Verifier(_transmitter.JwksUri, options => options.ExpectedAudiencesProvider = () => audiences);
        string set = _transmitter.SignSet(SessionRevoked());
        // any audience while the provider has none, for example before the stream is registered
        Assert.NotNull(await _verifier.VerifyAsync(set));
        audiences.Add("receiver/stream-1");
        await AssertRejectedAsync(set, SsfSetVerificationException.InvalidAudience);
        audiences.Add(TestTransmitter.Audience);
        Assert.NotNull(await _verifier.VerifyAsync(set));
    }

    [Fact]
    public async Task RejectsSetWithoutRequiredClaims()
    {
        JsonObject claims = SessionRevoked();
        claims.Remove("jti");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
        claims = SessionRevoked();
        claims.Remove("iat");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
        claims = SessionRevoked();
        claims["iat"] = "yesterday";
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
        claims = SessionRevoked();
        claims.Remove("events");
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
        claims = SessionRevoked();
        claims["events"] = new JsonObject();
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
    }

    [Fact]
    public async Task RejectsSetWithoutTopLevelSubjectUnlessAllowed()
    {
        // the subject in the event payload, as in earlier SSF drafts
        JsonObject payload = new()
        {
            ["subject"] = Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("s-1")),
            ["event_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        string legacy = _transmitter.SignSet(_transmitter.SetClaims("CaepSessionRevoked", null, payload));
        await AssertRejectedAsync(legacy, SsfSetVerificationException.InvalidRequest);
        JsonObject claims = SessionRevoked();
        claims["sub_id"] = new JsonObject();
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);

        _verifier.Options.SubjectCompatibility = SubjectCompatibilityMode.Legacy;
        Assert.Null((await _verifier.VerifyAsync(legacy)).SubjectId);
    }

    [Fact]
    public async Task RejectsSetIssuedInTheFuture()
    {
        JsonObject claims = SessionRevoked();
        claims["iat"] = DateTimeOffset.UtcNow.AddSeconds(600).ToUnixTimeSeconds();
        await AssertRejectedAsync(_transmitter.SignSet(claims), SsfSetVerificationException.InvalidRequest);
        // within the clock skew is fine
        claims["iat"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        Assert.NotNull(await _verifier.VerifyAsync(_transmitter.SignSet(claims)));
    }

    [Fact]
    public async Task UnreachableJwkSetIsReportedAsUnavailable()
    {
        using JwtSsfSetVerifier missing = Verifier(_transmitter.Issuer + "/missing-jwks");
        string set = _transmitter.SignSet(SessionRevoked());
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => missing.VerifyAsync(set));

        using HttpClient failing = new(new FailingHandler());
        using JwtSsfSetVerifier unreachable = new(_transmitter.Issuer, new Uri(_transmitter.JwksUri), failing);
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => unreachable.VerifyAsync(set));

        _transmitter.PublishDocument("/realms/test/not-a-jwks", "<html></html>");
        using JwtSsfSetVerifier garbage = Verifier(_transmitter.Issuer + "/not-a-jwks");
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => garbage.VerifyAsync(set));
    }

    [Fact]
    public async Task JwkSetLocationIsResolvedOnFirstUse()
    {
        int resolutions = 0;
        using JwtSsfSetVerifier verifier = new(_transmitter.Issuer, _ =>
        {
            resolutions++;
            return ValueTask.FromResult(new Uri(_transmitter.JwksUri));
        }, _http, new SsfSetVerifierOptions());
        Assert.Equal(0, resolutions);
        Assert.NotNull(await verifier.VerifyAsync(_transmitter.SignSet(SessionRevoked())));
        Assert.NotNull(await verifier.VerifyAsync(_transmitter.SignSet(SessionRevoked())));
        Assert.Equal(1, resolutions);

        using JwtSsfSetVerifier unresolvable = new(_transmitter.Issuer,
            _ => throw new SsfTransmitterUnavailableException("no metadata"), _http);
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(
            () => unresolvable.VerifyAsync(_transmitter.SignSet(SessionRevoked())));
    }

    [Fact]
    public async Task FetchesJwkSetAgainWhenSetNamesUnknownKeyButNotMoreOftenThanAllowed()
    {
        TestClock clock = new();
        _verifier = Verifier(_transmitter.JwksUri, options => options.TimeProvider = clock);
        Assert.NotNull(await _verifier.VerifyAsync(_transmitter.SignSet(SessionRevoked())));
        int requests = _transmitter.Requests;
        Assert.NotNull(await _verifier.VerifyAsync(_transmitter.SignSet(SessionRevoked())));
        Assert.Equal(requests, _transmitter.Requests);

        // the transmitter rotates its key: the first SET with the new kid triggers a fetch
        using TestSigningKey rotated = TestSigningKey.Generate(2048, "rotated-key");
        _transmitter.PublishJwks(_transmitter.Key, rotated);
        clock.Advance(TimeSpan.FromSeconds(31));
        string set = TestTransmitter.Sign(rotated, TestTransmitter.SecEventJwtType, SessionRevoked());
        Assert.NotNull(await _verifier.VerifyAsync(set));
        Assert.Equal(requests + 1, _transmitter.Requests);

        // a SET with a kid that is not there either is rejected, without a fetch while rate limited
        using TestSigningKey unknown = TestSigningKey.Generate(2048, "unknown-key");
        string unknownSet = TestTransmitter.Sign(unknown, TestTransmitter.SecEventJwtType, SessionRevoked());
        await AssertRejectedAsync(unknownSet, SsfSetVerificationException.InvalidKey);
        Assert.Equal(requests + 1, _transmitter.Requests);
        clock.Advance(TimeSpan.FromSeconds(31));
        await AssertRejectedAsync(unknownSet, SsfSetVerificationException.InvalidKey);
        Assert.Equal(requests + 2, _transmitter.Requests);
    }

    [Fact]
    public async Task FetchesJwkSetAgainWhenTheCacheExpires()
    {
        TestClock clock = new();
        _verifier = Verifier(_transmitter.JwksUri, options => options.TimeProvider = clock);
        string set = _transmitter.SignSet(SessionRevoked());
        Assert.NotNull(await _verifier.VerifyAsync(set));
        int requests = _transmitter.Requests;
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(await _verifier.VerifyAsync(set));
        Assert.Equal(requests, _transmitter.Requests);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await _verifier.VerifyAsync(set));
        Assert.Equal(requests + 1, _transmitter.Requests);
    }

    [Fact]
    public async Task VerifiesSet()
    {
        JsonObject claims = SessionRevoked();
        claims["txn"] = "txn-1";
        SsfEventToken eventToken = await _verifier.VerifyAsync(_transmitter.SignSet(claims));
        Assert.Equal(claims["jti"]!.GetValue<string>(), eventToken.Jti);
        Assert.Equal(_transmitter.Issuer, eventToken.Iss);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(claims["iat"]!.GetValue<long>()), eventToken.Iat);
        Assert.Equal([TestTransmitter.Audience], eventToken.Aud);
        Assert.Equal([SsfEventTypes.CaepSessionRevoked], eventToken.Events.Keys);
        Assert.Equal(JsonValueKind.Number, eventToken.Events[SsfEventTypes.CaepSessionRevoked].GetProperty("event_timestamp").ValueKind);
        Assert.Equal("complex", eventToken.SubjectId!.Value.GetProperty("format").GetString());
        Assert.Equal("txn-1", eventToken.Txn);
        Assert.Equal(["iss", "jti", "iat", "aud", "events", "sub_id", "txn"],
            eventToken.Claims.EnumerateObject().Select(p => p.Name));
        Assert.Equal("session-1", SsfSubject.From(eventToken.SubjectId).SessionId);
    }

    [Fact]
    public async Task VerifiesSetSignedWithRsaPss()
    {
        string set = TestTransmitter.Sign(_transmitter.Key, "PS256", TestTransmitter.SecEventJwtType, SessionRevoked());
        await AssertRejectedAsync(set, SsfSetVerificationException.InvalidKey);
        _verifier = Verifier(_transmitter.JwksUri, options => options.AcceptedAlgorithms.Add("PS256"));
        Assert.NotNull(await _verifier.VerifyAsync(set));
    }

    private async Task AssertRejectedAsync(string set, string errorCode)
    {
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => _verifier.VerifyAsync(set));
        Assert.Equal(errorCode, ex.ErrorCode);
    }

    private JsonObject SessionRevoked()
    {
        return _transmitter.SetClaims("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
