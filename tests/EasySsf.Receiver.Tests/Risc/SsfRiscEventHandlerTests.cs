using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Risc;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Risc;
using EasySsf.Receiver.Set;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Risc;

public sealed class SsfRiscEventHandlerTests(TestTransmitter transmitter) : IClassFixture<TestTransmitter>
{
    private const string UnknownRiscEvent = "https://schemas.openid.net/secevent/risc/event-type/account-merged";

    private readonly RecordingHandler _handler = new();

    [Fact]
    public async Task DispatchesEveryKindToItsMethod()
    {
        foreach (SsfRiscEventKind kind in Enum.GetValues<SsfRiscEventKind>())
        {
            string eventType = kind.EventType() ?? UnknownRiscEvent;
            await _handler.HandleAsync(Context(Contexts.Events(eventType, new JsonObject())));
        }
        Assert.Equal(["OnAccountCredentialChangeRequired", "OnAccountPurged", "OnAccountDisabled", "OnAccountEnabled",
            "OnIdentifierChanged", "OnIdentifierRecycled", "OnCredentialCompromise", "OnOptIn", "OnOptOutInitiated",
            "OnOptOutCancelled", "OnOptOutEffective", "OnRecoveryActivated", "OnRecoveryInformationChanged",
            "OnSessionsRevoked", "OnOtherRiscEvent"], _handler.Calls);
    }

    [Fact]
    public async Task HandlesAllRiscEventsOfASetAndIgnoresOtherSets()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject(),
            [SsfEventTypes.RiscAccountDisabled] = new JsonObject { ["reason"] = "hijacking" },
            [UnknownRiscEvent] = new JsonObject { ["into"] = "other-account" },
        };
        await _handler.HandleAsync(Context(events));
        await _handler.HandleAsync(Context(Contexts.Events(SsfEventTypes.CaepSessionRevoked, new JsonObject())));
        await _handler.HandleAsync(Context(Contexts.Events("urn:example:acme:login", new JsonObject())));
        Assert.Equal(["OnAccountDisabled", "OnOtherRiscEvent"], _handler.Calls);
        Assert.Equal("hijacking", _handler.Events[0].Reason);
        Assert.Equal(UnknownRiscEvent, _handler.Events[1].EventType);
        Assert.Equal("other-account", _handler.Events[1].Payload.GetProperty("into").GetString());
        Assert.Equal("john.doe@example.com", _handler.Contexts[0].Subject.Email);
    }

    [Fact]
    public async Task RiscSetPassesStrictVerificationAndReachesTheHandler()
    {
        transmitter.PublishJwks(transmitter.Key);
        using HttpClient http = transmitter.CreateHttpClient();
        SsfSetVerifierOptions options = new();
        options.ExpectedAudiences.Add(TestTransmitter.Audience);
        using JwtSsfSetVerifier verifier = new(transmitter.Issuer, new Uri(transmitter.JwksUri), http, options);
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(10), [_handler]);
        JsonObject claims = transmitter.SetClaims("RiscCredentialCompromise",
            IssSub("https://idp.example.com/3456790/", "joe.smith@example.com"),
            new JsonObject { ["credential_type"] = "password", ["event_timestamp"] = 1508184845 });

        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync(transmitter.SignSet(claims)));

        Assert.Equal(["OnCredentialCompromise"], _handler.Calls);
        SsfRiscEvent riscEvent = _handler.Events[0];
        Assert.Equal("password", riscEvent.CredentialType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1508184845), riscEvent.EventTimestamp);
        Assert.Equal("joe.smith@example.com", _handler.Contexts[0].Subject.Subject);
    }

    private static SsfEventContext Context(JsonObject events)
    {
        return Contexts.Of(events, Email("john.doe@example.com"));
    }

    private sealed class RecordingHandler : SsfRiscEventHandler
    {
        public List<string> Calls { get; } = [];

        public List<SsfRiscEvent> Events { get; } = [];

        public List<SsfEventContext> Contexts { get; } = [];

        protected override Task OnAccountCredentialChangeRequiredAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAccountCredentialChangeRequired", riscEvent, eventContext);
        }

        protected override Task OnAccountPurgedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAccountPurged", riscEvent, eventContext);
        }

        protected override Task OnAccountDisabledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAccountDisabled", riscEvent, eventContext);
        }

        protected override Task OnAccountEnabledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAccountEnabled", riscEvent, eventContext);
        }

        protected override Task OnIdentifierChangedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnIdentifierChanged", riscEvent, eventContext);
        }

        protected override Task OnIdentifierRecycledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnIdentifierRecycled", riscEvent, eventContext);
        }

        protected override Task OnCredentialCompromiseAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnCredentialCompromise", riscEvent, eventContext);
        }

        protected override Task OnOptInAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOptIn", riscEvent, eventContext);
        }

        protected override Task OnOptOutInitiatedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOptOutInitiated", riscEvent, eventContext);
        }

        protected override Task OnOptOutCancelledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOptOutCancelled", riscEvent, eventContext);
        }

        protected override Task OnOptOutEffectiveAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOptOutEffective", riscEvent, eventContext);
        }

        protected override Task OnRecoveryActivatedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnRecoveryActivated", riscEvent, eventContext);
        }

        protected override Task OnRecoveryInformationChangedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnRecoveryInformationChanged", riscEvent, eventContext);
        }

        protected override Task OnSessionsRevokedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnSessionsRevoked", riscEvent, eventContext);
        }

        protected override Task OnOtherRiscEventAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnOtherRiscEvent", riscEvent, eventContext);
        }

        private Task Record(string method, SsfRiscEvent riscEvent, SsfEventContext eventContext)
        {
            Calls.Add(method);
            Events.Add(riscEvent);
            Contexts.Add(eventContext);
            return Task.CompletedTask;
        }
    }
}
