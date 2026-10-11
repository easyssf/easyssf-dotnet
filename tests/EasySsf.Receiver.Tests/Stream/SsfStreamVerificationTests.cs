using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Tests.Set;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Stream;

public class SsfStreamVerificationTests
{
    private const string StreamId = "stream-1";

    private readonly SsfStreamVerification _verification = new() { StreamId = () => StreamId };

    [Fact]
    public void AcceptsVerificationEventThatEchoesTheRequestedState()
    {
        string state = _verification.NewState();
        Assert.True(_verification.IsPending);
        _verification.Validate(VerificationEvent(StreamId, state));
        Assert.False(_verification.IsPending);
    }

    [Fact]
    public void RejectsVerificationEventWithAnotherStateThanTheRequestedOne()
    {
        string state = _verification.NewState();
        AssertRejected(VerificationEvent(StreamId, "made-up"), SsfSetVerificationException.InvalidState);
        // the requested verification is still outstanding
        _verification.Validate(VerificationEvent(StreamId, state));
    }

    [Fact]
    public void AcceptsVerificationEventInitiatedByTheTransmitter()
    {
        _verification.NewState();
        _verification.Validate(VerificationEvent(StreamId, null));
        Assert.True(_verification.IsPending);
    }

    [Fact]
    public void AcceptsStateWhenNoVerificationWasRequestedUnlessStrict()
    {
        _verification.Validate(VerificationEvent(StreamId, "made-up"));
        _verification.RejectUnrequestedState = true;
        AssertRejected(VerificationEvent(StreamId, "made-up"), SsfSetVerificationException.InvalidState);
    }

    [Fact]
    public void RejectsVerificationEventAboutAnotherStream()
    {
        string state = _verification.NewState();
        AssertRejected(VerificationEvent("another-stream", state), SsfSetVerificationException.InvalidRequest);
        AssertRejected(new SsfEventContext(Token("jti", SsfEventTypes.SsfStreamVerification, new JsonObject { ["state"] = state },
            IssSub("https://idp.example", "alice"))), SsfSetVerificationException.InvalidRequest);
        // the state was not used up by the rejected events
        _verification.Validate(VerificationEvent(StreamId, state));
    }

    [Fact]
    public void RejectsVerificationEventWithoutSubjectId()
    {
        string state = _verification.NewState();
        AssertRejected(new SsfEventContext(Token("jti", SsfEventTypes.SsfStreamVerification, new JsonObject { ["state"] = state }, null)),
            SsfSetVerificationException.InvalidRequest);
        _verification.Validate(VerificationEvent(StreamId, state));
    }

    [Fact]
    public void DoesNotCheckTheStreamWhileItIsUnknown()
    {
        SsfStreamVerification unknownStream = new();
        unknownStream.Validate(VerificationEvent("any-stream", null));
    }

    [Fact]
    public void IgnoresOtherEvents()
    {
        _verification.NewState();
        SsfEventContext sessionRevoked = new(Token("jti", SsfEventTypes.CaepSessionRevoked, new JsonObject { ["state"] = "x" },
            Opaque("another-stream")));
        _verification.Validate(sessionRevoked);
    }

    [Fact]
    public async Task ProcessorRejectsInvalidVerificationEventWithoutHandlingOrRememberingIt()
    {
        List<string> handled = [];
        ISsfEventHandler handler = SsfEventHandler.Of((context, _) =>
        {
            handled.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        });
        Dictionary<string, SsfEventToken> sets = new(StringComparer.Ordinal);
        SsfSetProcessor processor = new(new DelegateVerifier(set => sets[set]), new InMemorySsfJtiDedupStore(10), [handler],
            new SsfSetProcessorOptions { StreamVerifications = _ => _verification });
        string state = _verification.NewState();
        sets["wrong"] = VerificationEvent(StreamId, "made-up").EventToken;
        sets["right"] = VerificationEvent(StreamId, state).EventToken;

        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => processor.ProcessAsync("wrong"));
        Assert.Equal(SsfSetVerificationException.InvalidState, ex.ErrorCode);
        Assert.Empty(handled);

        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("right"));
        Assert.Single(handled);
    }

    private void AssertRejected(SsfEventContext eventContext, string errorCode)
    {
        SsfSetVerificationException ex = Assert.Throws<SsfSetVerificationException>(() => _verification.Validate(eventContext));
        Assert.Equal(errorCode, ex.ErrorCode);
    }

    private static SsfEventContext VerificationEvent(string streamId, string? state)
    {
        JsonObject payload = (state is not null) ? new JsonObject { ["state"] = state } : new JsonObject();
        return new SsfEventContext(Token("jti-" + streamId + "-" + state, SsfEventTypes.SsfStreamVerification, payload, Opaque(streamId)));
    }

    private static SsfEventToken Token(string jti, string eventType, JsonObject payload, JsonObject? subjectId)
    {
        List<KeyValuePair<string, JsonElement>> events = [new(eventType, SsfJson.ToElement(payload))];
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventToken(jti, "https://idp.example", DateTimeOffset.UtcNow, null, events, subject, null, SsfJson.EmptyObject);
    }
}
