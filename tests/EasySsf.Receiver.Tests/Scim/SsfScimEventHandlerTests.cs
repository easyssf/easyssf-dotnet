using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Scim;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Scim;
using EasySsf.Receiver.Set;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Scim;

public sealed class SsfScimEventHandlerTests(TestTransmitter transmitter) : IClassFixture<TestTransmitter>
{
    private const string UserUri = "/Users/2b2f880af6674ac284bae9381673d462";

    private readonly RecordingHandler _handler = new();

    [Fact]
    public async Task DispatchesEveryOperationToItsMethod()
    {
        foreach (SsfScimOperation operation in Enum.GetValues<SsfScimOperation>())
        {
            foreach (string eventType in operation.EventTypes())
            {
                await _handler.HandleAsync(Contexts.Of(Contexts.Events(eventType, new JsonObject()), SsfSubjectIdentifiers.Scim(UserUri), "https://scim.example.com"));
            }
        }
        Assert.Equal(["OnFeedAdd", "OnFeedRemove", "OnCreate", "OnCreate", "OnPatch", "OnPatch", "OnPut", "OnPut", "OnDelete",
            "OnActivate", "OnDeactivate", "OnAsyncResponse"], _handler.Calls);
    }

    [Fact]
    public async Task HandlesAllScimEventsOfASetAndIgnoresOtherSets()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.ScimProvCreateFull] = new JsonObject { ["data"] = new JsonObject { ["userName"] = "jdoe" } },
            [SsfEventTypes.ScimFeedAdd] = new JsonObject(),
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject(),
        };
        await _handler.HandleAsync(Contexts.Of(events, SsfSubjectIdentifiers.Scim(UserUri, "jdoe"), "https://scim.example.com"));
        await _handler.HandleAsync(Contexts.Of(Contexts.Events(SsfEventTypes.CaepSessionRevoked, new JsonObject()), Opaque("abc")));
        Assert.Equal(["OnCreate", "OnFeedAdd"], _handler.Calls);
        Assert.All(_handler.Events, scimEvent =>
        {
            Assert.Equal("jdoe", scimEvent.Subject!.ExternalId);
            Assert.Equal("Users", scimEvent.Subject.ResourceType);
        });
        Assert.Equal("jdoe", _handler.Events[0].Data!.Value.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task ScimSetPassesStrictVerificationAndReachesTheHandler()
    {
        transmitter.PublishJwks(transmitter.Key);
        using HttpClient http = transmitter.CreateHttpClient();
        SsfSetVerifierOptions options = new();
        options.ExpectedAudiences.Add(TestTransmitter.Audience);
        using JwtSsfSetVerifier verifier = new(transmitter.Issuer, new Uri(transmitter.JwksUri), http, options);
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(10), [_handler]);
        JsonObject claims = transmitter.SetClaims("ScimProvPatchNotice", SsfSubjectIdentifiers.Scim(UserUri, "jdoe"),
            new JsonObject { ["attributes"] = new JsonArray("members"), ["version"] = "a330bc54f0671c9" });
        claims["txn"] = "b7b953f11cc6489bbfb87834747cc4c1";

        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync(transmitter.SignSet(claims)));

        Assert.Equal(["OnPatch"], _handler.Calls);
        SsfScimEvent scimEvent = _handler.Events[0];
        Assert.True(scimEvent.IsNotice);
        Assert.Equal(["members"], scimEvent.Attributes);
        Assert.Equal("a330bc54f0671c9", scimEvent.Version);
        Assert.Equal("2b2f880af6674ac284bae9381673d462", scimEvent.Subject!.Id);
        Assert.Equal("b7b953f11cc6489bbfb87834747cc4c1", _handler.Contexts[0].EventToken.Txn);
    }

    private sealed class RecordingHandler : SsfScimEventHandler
    {
        public List<string> Calls { get; } = [];

        public List<SsfScimEvent> Events { get; } = [];

        public List<SsfEventContext> Contexts { get; } = [];

        protected override Task OnFeedAddAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnFeedAdd", scimEvent, eventContext);
        }

        protected override Task OnFeedRemoveAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnFeedRemove", scimEvent, eventContext);
        }

        protected override Task OnCreateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnCreate", scimEvent, eventContext);
        }

        protected override Task OnPatchAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnPatch", scimEvent, eventContext);
        }

        protected override Task OnPutAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnPut", scimEvent, eventContext);
        }

        protected override Task OnDeleteAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnDelete", scimEvent, eventContext);
        }

        protected override Task OnActivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnActivate", scimEvent, eventContext);
        }

        protected override Task OnDeactivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnDeactivate", scimEvent, eventContext);
        }

        protected override Task OnAsyncResponseAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
        {
            return Record("OnAsyncResponse", scimEvent, eventContext);
        }

        private Task Record(string method, SsfScimEvent scimEvent, SsfEventContext eventContext)
        {
            Calls.Add(method);
            Events.Add(scimEvent);
            Contexts.Add(eventContext);
            return Task.CompletedTask;
        }
    }
}
