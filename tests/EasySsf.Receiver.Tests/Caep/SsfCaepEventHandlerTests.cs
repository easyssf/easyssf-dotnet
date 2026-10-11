using System.Text.Json.Nodes;

using EasySsf.Core.Caep;
using EasySsf.Core.Event;
using EasySsf.Receiver.Caep;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Set;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Caep;

public sealed class SsfCaepEventHandlerTests(TestTransmitter transmitter) : IClassFixture<TestTransmitter>
{
    private const string UnknownCaepEvent = "https://schemas.openid.net/secevent/caep/event-type/geo-fence-exit";

    private readonly RecordingHandler _handler = new();

    [Fact]
    public async Task DispatchesEveryKindToItsMethod()
    {
        foreach (SsfCaepEventKind kind in Enum.GetValues<SsfCaepEventKind>())
        {
            string eventType = kind.EventType() ?? UnknownCaepEvent;
            await _handler.HandleAsync(Context(Contexts.Events(eventType, new JsonObject())));
        }
        Assert.Equal(["OnSessionRevoked", "OnTokenClaimsChange", "OnCredentialChange", "OnAssuranceLevelChange",
            "OnDeviceComplianceChange", "OnSessionEstablished", "OnSessionPresented", "OnRiskLevelChange", "OnOtherCaepEvent"],
            _handler.Calls);
    }

    [Fact]
    public async Task HandlesAllCaepEventsOfASetAndIgnoresOtherSets()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject { ["initiating_entity"] = "admin" },
            [SsfEventTypes.RiscAccountDisabled] = new JsonObject { ["reason"] = "hijacking" },
            [UnknownCaepEvent] = new JsonObject { ["event_timestamp"] = 1615304991, ["zone"] = "HQ" },
        };
        await _handler.HandleAsync(Context(events));
        await _handler.HandleAsync(Context(Contexts.Events(SsfEventTypes.RiscAccountPurged, new JsonObject())));
        await _handler.HandleAsync(Context(Contexts.Events("urn:example:acme:login", new JsonObject())));
        Assert.Equal(["OnSessionRevoked", "OnOtherCaepEvent"], _handler.Calls);
        Assert.Equal("admin", _handler.Events[0].InitiatingEntity);
        SsfCaepEvent other = _handler.Events[1];
        Assert.Equal(UnknownCaepEvent, other.EventType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1615304991), other.EventTimestamp);
        Assert.Equal("HQ", other.Payload.GetProperty("zone").GetString());
        Assert.Equal("someuser@somedomain.com", _handler.Contexts[0].Subject.Email);
    }

    [Fact]
    public async Task CaepSetPassesStrictVerificationAndReachesTheHandler()
    {
        transmitter.PublishJwks(transmitter.Key);
        using HttpClient http = transmitter.CreateHttpClient();
        SsfSetVerifierOptions options = new();
        options.ExpectedAudiences.Add(TestTransmitter.Audience);
        using JwtSsfSetVerifier verifier = new(transmitter.Issuer, new Uri(transmitter.JwksUri), http, options);
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(10), [_handler]);
        JsonObject claims = transmitter.SetClaims("CaepAssuranceLevelChange",
            IssSub("https://idp.example.com/3456789/", "jane.smith@example.com"),
            new JsonObject
            {
                ["namespace"] = "NIST-AAL",
                ["current_level"] = "nist-aal1",
                ["previous_level"] = "nist-aal2",
                ["change_direction"] = "decrease",
                ["initiating_entity"] = "user",
                ["event_timestamp"] = 1615304991,
            });
        claims["txn"] = "8675309";

        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync(transmitter.SignSet(claims)));

        Assert.Equal(["OnAssuranceLevelChange"], _handler.Calls);
        SsfCaepEvent caepEvent = _handler.Events[0];
        Assert.Equal("NIST-AAL", caepEvent.Namespace);
        Assert.Equal("nist-aal1", caepEvent.CurrentLevel);
        Assert.Equal("nist-aal2", caepEvent.PreviousLevel);
        Assert.Equal("decrease", caepEvent.ChangeDirection);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1615304991), caepEvent.EventTimestamp);
        SsfEventContext eventContext = _handler.Contexts[0];
        Assert.Equal("jane.smith@example.com", eventContext.Subject.Subject);
        Assert.Equal("8675309", eventContext.EventToken.Txn);
    }

    private static SsfEventContext Context(JsonObject events)
    {
        return Contexts.Of(events, Email("someuser@somedomain.com"));
    }

    private sealed class RecordingHandler : SsfCaepEventHandler
    {
        public List<string> Calls { get; } = [];

        public List<SsfCaepEvent> Events { get; } = [];

        public List<SsfEventContext> Contexts { get; } = [];

        protected override Task OnSessionRevokedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnSessionRevoked", caepEvent, eventContext);
        }

        protected override Task OnTokenClaimsChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnTokenClaimsChange", caepEvent, eventContext);
        }

        protected override Task OnCredentialChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnCredentialChange", caepEvent, eventContext);
        }

        protected override Task OnAssuranceLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAssuranceLevelChange", caepEvent, eventContext);
        }

        protected override Task OnDeviceComplianceChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnDeviceComplianceChange", caepEvent, eventContext);
        }

        protected override Task OnSessionEstablishedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnSessionEstablished", caepEvent, eventContext);
        }

        protected override Task OnSessionPresentedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnSessionPresented", caepEvent, eventContext);
        }

        protected override Task OnRiskLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnRiskLevelChange", caepEvent, eventContext);
        }

        protected override Task OnOtherCaepEventAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOtherCaepEvent", caepEvent, eventContext);
        }

        private Task Record(string method, SsfCaepEvent caepEvent, SsfEventContext eventContext)
        {
            Calls.Add(method);
            Events.Add(caepEvent);
            Contexts.Add(eventContext);
            return Task.CompletedTask;
        }
    }
}
