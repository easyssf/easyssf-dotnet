using System.Text;
using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Tests.Set;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Push;

/// <summary>
/// A receiver assembled by hand, the way an application without a framework integration would do
/// it.
/// </summary>
public sealed class SsfPushHandlerTests : IClassFixture<TestTransmitter>
{
    private const string Authorization = "Bearer push-secret";

    private readonly TestTransmitter _transmitter;

    private readonly List<SsfSubject> _revoked = [];

    private readonly Queue<Exception> _failures = new();

    private readonly RecordingMetrics _metrics = new();

    private readonly SsfPushHandler _pushHandler;

    public SsfPushHandlerTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _transmitter.PublishJwks(_transmitter.Key);
        _pushHandler = PushHandler(_transmitter, Authorization);
    }

    private SsfPushHandler PushHandler(TestTransmitter transmitter, string? authorization)
    {
        HttpClient http = transmitter.CreateHttpClient();
        SsfSetVerifierOptions verifierOptions = new();
        verifierOptions.ExpectedAudiences.Add(TestTransmitter.Audience);
        // the JWK Set location is resolved on first use, as the metadata resolver will do it
        JwtSsfSetVerifier verifier = new(transmitter.Issuer, _ => ValueTask.FromResult(new Uri(transmitter.JwksUri)), http,
            verifierOptions);
        ISsfEventHandler handler = SsfEventHandler.Of((eventContext, _) =>
        {
            if (_failures.TryDequeue(out Exception? failure))
            {
                throw failure;
            }
            if (eventContext.HasEvent("CaepSessionRevoked"))
            {
                _revoked.Add(eventContext.SubjectFor("CaepSessionRevoked"));
            }
            return Task.CompletedTask;
        });
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(100), [handler],
            new SsfSetProcessorOptions { Metrics = _metrics });
        return new SsfPushHandler(processor, authorization, _metrics);
    }

    [Fact]
    public async Task AcceptsAndHandlesSet()
    {
        SsfPushResponse response = await PushAsync(Authorization, SessionRevoked("session-1"));
        Assert.Equal(202, response.Status);
        Assert.True(response.IsAccepted);
        Assert.Null(response.Body);
        Assert.Equal(["session-1"], _revoked.Select(s => s.SessionId));
        Assert.Equal(["alice"], _revoked.Select(s => s.Subject));
    }

    [Fact]
    public async Task AcceptsDuplicateWithoutHandlingItAgain()
    {
        string set = SessionRevoked("session-2");
        Assert.Equal(202, (await PushAsync(Authorization, set)).Status);
        Assert.Equal(202, (await PushAsync(Authorization, set)).Status);
        Assert.Single(_revoked);
    }

    [Fact]
    public async Task RejectsTransmitterThatDoesNotAuthenticate()
    {
        foreach (string? authorization in new[] { null, "Bearer wrong", "Bearer push-secret " })
        {
            SsfPushResponse response = await PushAsync(authorization, SessionRevoked("session-3"));
            Assert.Equal(401, response.Status);
            Assert.False(response.IsAccepted);
            Assert.Equal("authentication_failed", Error(response)["err"]!.GetValue<string>());
        }
        Assert.Empty(_revoked);
        Assert.Equal(3, _metrics.Sets.Count(s => s.Outcome == SsfSetOutcome.Unauthenticated));
    }

    [Fact]
    public async Task AcceptsAnyTransmitterWhenNoHeaderIsExpected()
    {
        SsfPushHandler open = PushHandler(_transmitter, null);
        Assert.Equal(202, (await open.HandleAsync(null, Bytes(SessionRevoked("session-open")))).Status);
    }

    [Fact]
    public async Task LooksUpTheHeaderByTheIssuerForSeveralTransmitters()
    {
        SsfPushHandler routing = new(Processor(), issuer => issuer switch
        {
            string iss when iss == _transmitter.Issuer => "Bearer this-transmitter",
            _ => "Bearer nobody",
        }, _metrics);
        Assert.Equal(202, (await routing.HandleAsync("Bearer this-transmitter", Bytes(SessionRevoked("session-r1")))).Status);
        Assert.Equal(401, (await routing.HandleAsync("Bearer push-secret", Bytes(SessionRevoked("session-r2")))).Status);
        // a SET without a readable issuer is checked against the header for an unknown issuer
        SsfPushResponse unreadable = await routing.HandleAsync("Bearer this-transmitter", Bytes("not-a-set"));
        Assert.Equal(401, unreadable.Status);
        Assert.Equal((null, SsfDeliveryMethod.Push, SsfSetOutcome.Unauthenticated), _metrics.Sets[^1]);
    }

    [Fact]
    public async Task RejectsInvalidSetWithErrorDocument()
    {
        SsfPushResponse malformed = await PushAsync(Authorization, "not-a-set");
        Assert.Equal(400, malformed.Status);
        JsonObject error = Error(malformed);
        Assert.Equal("invalid_request", error["err"]!.GetValue<string>());
        Assert.True(error.ContainsKey("description"));

        JsonObject claims = _transmitter.SetClaims("CaepSessionRevoked", Opaque("x"));
        claims["aud"] = "https://other.example";
        SsfPushResponse wrongAudience = await PushAsync(Authorization, _transmitter.SignSet(claims));
        Assert.Equal(400, wrongAudience.Status);
        Assert.Equal("invalid_audience", Error(wrongAudience)["err"]!.GetValue<string>());
        Assert.Equal(SsfSetOutcome.Invalid, _metrics.Sets[^1].Outcome);
    }

    [Fact]
    public async Task RejectsOversizedSet()
    {
        byte[] body = new byte[SsfPushHandler.MaxSetSize + 1];
        SsfPushResponse response = await _pushHandler.HandleAsync(Authorization, body);
        Assert.Equal(413, response.Status);
        Assert.Equal("invalid_request", Error(response)["err"]!.GetValue<string>());
    }

    [Fact]
    public async Task AsksForRedeliveryWhenHandlerFails()
    {
        string set = SessionRevoked("session-4");
        _failures.Enqueue(new InvalidOperationException("store is down"));
        Assert.Equal(500, (await PushAsync(Authorization, set)).Status);
        Assert.Equal(202, (await PushAsync(Authorization, set)).Status);
        Assert.Single(_revoked);
    }

    [Fact]
    public async Task AsksForRedeliveryWhileTransmitterKeysAreUnavailable()
    {
        using TestTransmitter unavailable = new();
        unavailable.RemoveDocument("/realms/test/jwks");
        SsfPushHandler handler = PushHandler(unavailable, Authorization);
        string set = unavailable.Set("CaepSessionRevoked", Complex(IssSub(unavailable.Issuer, "alice"), Opaque("session-5")));
        SsfPushResponse response = await handler.HandleAsync(Authorization, Bytes(set));
        Assert.Equal(503, response.Status);
        Assert.Null(response.Body);
        unavailable.PublishJwks(unavailable.Key);
        Assert.Equal(202, (await handler.HandleAsync(Authorization, Bytes(set))).Status);
    }

    private SsfSetProcessor Processor()
    {
        HttpClient http = _transmitter.CreateHttpClient();
        JwtSsfSetVerifier verifier = new(_transmitter.Issuer, new Uri(_transmitter.JwksUri), http);
        return new SsfSetProcessor(verifier, new InMemorySsfJtiDedupStore(100), [], new SsfSetProcessorOptions { Metrics = _metrics });
    }

    private Task<SsfPushResponse> PushAsync(string? authorization, string set)
    {
        return _pushHandler.HandleAsync(authorization, Bytes(set));
    }

    private static byte[] Bytes(string set)
    {
        return Encoding.UTF8.GetBytes(set);
    }

    private string SessionRevoked(string sessionId)
    {
        return _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque(sessionId)));
    }

    private static JsonObject Error(SsfPushResponse response)
    {
        Assert.NotNull(response.Body);
        return (JsonObject)JsonNode.Parse(response.Body)!;
    }
}
