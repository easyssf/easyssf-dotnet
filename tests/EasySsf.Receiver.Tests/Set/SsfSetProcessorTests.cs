using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;

namespace EasySsf.Receiver.Tests.Set;

public class SsfSetProcessorTests
{
    private static readonly KeyValuePair<string, JsonElement>[] SessionRevoked =
        [new(SsfEventTypes.CaepSessionRevoked, SsfJson.EmptyObject)];

    private readonly List<string> _handled = [];

    private readonly Queue<Exception> _failures = new();

    private readonly DelegateVerifier _verifier = new(encodedSet => new SsfEventToken(encodedSet, "https://idp.example",
        DateTimeOffset.UtcNow, null, SessionRevoked, null, null, SsfJson.EmptyObject));

    private readonly RecordingMetrics _metrics = new();

    private readonly ISsfEventHandler _recordingHandler;

    public SsfSetProcessorTests()
    {
        _recordingHandler = SsfEventHandler.Of((context, _) =>
        {
            _handled.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task HandsVerifiedSetToAllHandlers()
    {
        SsfSetProcessor processor = Processor(_recordingHandler, _recordingHandler);
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-1"));
        Assert.Equal(["jti-1", "jti-1"], _handled);
        Assert.Equal([("https://idp.example", SsfDeliveryMethod.Push, SsfSetOutcome.Handled)], _metrics.Sets);
        Assert.Equal([("https://idp.example", SsfEventTypes.CaepSessionRevoked, SsfDeliveryMethod.Push)], _metrics.Events);
    }

    [Fact]
    public async Task SkipsSetThatWasProcessedBefore()
    {
        SsfSetProcessor processor = Processor(_recordingHandler);
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-1"));
        Assert.Equal(SsfSetProcessingOutcome.Duplicate, await processor.ProcessAsync("jti-1", SsfDeliveryMethod.Poll, default));
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-2"));
        Assert.Equal(["jti-1", "jti-2"], _handled);
        Assert.Equal(SsfSetOutcome.Duplicate, _metrics.Sets[1].Outcome);
        Assert.Equal(SsfDeliveryMethod.Poll, _metrics.Sets[1].Delivery);
    }

    [Fact]
    public async Task ProcessesDuplicatesWithoutDedupStore()
    {
        SsfSetProcessor processor = new(_verifier, null, [_recordingHandler]);
        await processor.ProcessAsync("jti-1");
        await processor.ProcessAsync("jti-1");
        Assert.Equal(["jti-1", "jti-1"], _handled);
    }

    [Fact]
    public async Task FailingHandlerDoesNotKeepOthersFromHandlingAndAllowsRedelivery()
    {
        _failures.Enqueue(new InvalidOperationException("store is down"));
        SsfSetProcessor processor = Processor(FailingOnce(record: false), _recordingHandler);
        SsfEventHandlingException ex = await Assert.ThrowsAsync<SsfEventHandlingException>(() => processor.ProcessAsync("jti-1"));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(["jti-1"], _handled);
        Assert.Equal(SsfSetOutcome.Failed, _metrics.Sets[0].Outcome);
        // the SET is delivered again and must not be skipped as a duplicate
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-1"));
        Assert.Equal(["jti-1", "jti-1"], _handled);
    }

    [Fact]
    public async Task SetBeingHandledElsewhereIsLeftForARedelivery()
    {
        SsfSetProcessor processor = new(_verifier, new InProgressStore(), [_recordingHandler], Options());
        SsfSetInProgressException ex = await Assert.ThrowsAsync<SsfSetInProgressException>(() => processor.ProcessAsync("jti-1"));
        Assert.Contains("another instance", ex.Message);
        Assert.Empty(_handled);
        Assert.Equal(SsfSetOutcome.InProgress, _metrics.Sets[0].Outcome);
    }

    [Fact]
    public async Task HandledSetIsMarkedProcessedAndFailedOneForgotten()
    {
        InMemorySsfJtiDedupStore store = new(10);
        _failures.Enqueue(new InvalidOperationException("store is down"));
        SsfSetProcessor processor = new(_verifier, store, [FailingOnce()]);
        await Assert.ThrowsAsync<SsfEventHandlingException>(() => processor.ProcessAsync("jti-1"));
        // forgotten: the redelivery is new, not in progress
        SsfJtiClaim claim = await store.ClaimAsync(_verifier.Verify("jti-1"));
        Assert.True(claim.IsNew);
        await store.ForgetAsync(_verifier.Verify("jti-1"), claim);
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-1"));
        Assert.Equal(SsfJtiClaim.Processed, await store.ClaimAsync(_verifier.Verify("jti-1")));
    }

    [Fact]
    public async Task CancelledHandlerLeavesSetForARedelivery()
    {
        InMemorySsfJtiDedupStore store = new(10);
        using CancellationTokenSource cancellation = new();
        ISsfEventHandler cancelling = SsfEventHandler.Of((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        SsfSetProcessor processor = new(_verifier, store, [cancelling, _recordingHandler]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync("jti-1", cancellation.Token));
        Assert.Empty(_handled);
        Assert.True((await store.ClaimAsync(_verifier.Verify("jti-1"))).IsNew);
    }

    [Fact]
    public async Task InvalidSetAndUnavailableTransmitterAreCountedByTheUnverifiedIssuer()
    {
        DelegateVerifier rejecting = new(_ => throw new SsfSetVerificationException(SsfSetVerificationException.InvalidKey, "bad key"));
        SsfSetProcessor processor = new(rejecting, null, [_recordingHandler], Options());
        string set = Testing.TestTransmitter.Unsigned(new JsonObject { ["iss"] = "https://unverified.example" });
        await Assert.ThrowsAsync<SsfSetVerificationException>(() => processor.ProcessAsync(set));
        Assert.Equal(("https://unverified.example", SsfDeliveryMethod.Push, SsfSetOutcome.Invalid), _metrics.Sets[0]);

        DelegateVerifier unavailable = new(_ => throw new Transmitter.SsfTransmitterUnavailableException("no keys"));
        processor = new SsfSetProcessor(unavailable, null, [_recordingHandler], Options());
        await Assert.ThrowsAsync<Transmitter.SsfTransmitterUnavailableException>(() => processor.ProcessAsync("not-a-set"));
        Assert.Equal((null, SsfDeliveryMethod.Push, SsfSetOutcome.Unavailable), _metrics.Sets[1]);
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task StreamVerificationOfTheIssuerValidatesTheSetBeforeHandlers()
    {
        RejectingVerification rejecting = new();
        SsfSetProcessorOptions options = Options();
        options.StreamVerifications = issuer => (issuer == "https://idp.example") ? rejecting : null;
        SsfSetProcessor processor = new(_verifier, null, [_recordingHandler], options);
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => processor.ProcessAsync("jti-1"));
        Assert.Equal(SsfSetVerificationException.InvalidState, ex.ErrorCode);
        Assert.Equal(["jti-1"], rejecting.Seen);
        Assert.Empty(_handled);
        Assert.Equal(SsfSetOutcome.Invalid, _metrics.Sets[0].Outcome);
    }

    [Fact]
    public async Task RejectsSetWithCriticalSubjectMemberTheReceiverDoesNotUnderstand()
    {
        JsonElement subjectId = SsfJson.ToElement(new JsonObject
        {
            ["format"] = "complex",
            ["user"] = new JsonObject { ["format"] = "email", ["email"] = "alice@example.com" },
            ["tenant"] = new JsonObject { ["format"] = "opaque", ["id"] = "t1" },
            ["custom"] = new JsonObject { ["format"] = "acme-custom", ["ref"] = "x" },
        });
        DelegateVerifier verifier = new(encodedSet => new SsfEventToken(encodedSet, "https://idp.example", DateTimeOffset.UtcNow,
            null, SessionRevoked, subjectId, null, SsfJson.EmptyObject));
        SsfSetProcessor processor = new(verifier, null, [_recordingHandler]);

        // nothing declared critical: anything goes
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-1"));

        // critical members this receiver models are fine, a critical member it does not model is not
        processor.Options.CriticalSubjectMembers = _ => ["tenant"];
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-2"));
        processor.Options.CriticalSubjectMembers = _ => ["tenant", "custom"];
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => processor.ProcessAsync("jti-3"));
        Assert.Equal(SsfSetVerificationException.InvalidRequest, ex.ErrorCode);
        Assert.Contains("custom", ex.Message);

        // unless the application says it understands it
        processor.Options.UnderstoodSubjectMembers.Add("custom");
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-4"));
        // a critical member that is absent from the subject does not matter
        processor.Options.CriticalSubjectMembers = _ => ["device"];
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync("jti-5"));
        Assert.Equal(["jti-1", "jti-2", "jti-4", "jti-5"], _handled);
    }

    [Fact]
    public async Task HandlerForAnEventTypeOnlySeesSetsWithThatEvent()
    {
        List<string> revoked = [];
        ISsfEventHandler onRevoked = SsfEventHandler.For("CaepSessionRevoked", (context, _) =>
        {
            revoked.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        });
        List<string> changed = [];
        ISsfEventHandler onChange = SsfEventHandler.For(SsfEventTypes.CaepCredentialChange, (context, _) =>
        {
            changed.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        });
        SsfSetProcessor processor = new(_verifier, null, [onRevoked, onChange]);
        await processor.ProcessAsync("jti-1");
        Assert.Equal(["jti-1"], revoked);
        Assert.Empty(changed);
        Assert.Equal("SsfEventHandler.For(CaepCredentialChange)", onChange.ToString());
    }

    private SsfSetProcessor Processor(params ISsfEventHandler[] handlers)
    {
        return new SsfSetProcessor(_verifier, new InMemorySsfJtiDedupStore(100), handlers, Options());
    }

    private SsfSetProcessorOptions Options()
    {
        return new SsfSetProcessorOptions { Metrics = _metrics };
    }

    private ISsfEventHandler FailingOnce(bool record = true)
    {
        return SsfEventHandler.Of((context, _) =>
        {
            if (_failures.TryDequeue(out Exception? failure))
            {
                throw failure;
            }
            if (record)
            {
                _handled.Add(context.EventToken.Jti);
            }
            return Task.CompletedTask;
        });
    }

    private sealed class InProgressStore : ISsfJtiDedupStore
    {
        public ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(SsfJtiClaim.InProgress);
        }

        public ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RejectingVerification : ISsfStreamVerification
    {
        public List<string> Seen { get; } = [];

        public void Validate(SsfEventContext eventContext)
        {
            Seen.Add(eventContext.EventToken.Jti);
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidState, "not my stream");
        }
    }
}

/// <summary>
/// Records the metrics calls, for assertions.
/// </summary>
public sealed class RecordingMetrics : ISsfReceiverMetrics
{
    public List<(string? Transmitter, SsfDeliveryMethod Delivery, SsfSetOutcome Outcome)> Sets { get; } = [];

    public List<(string Transmitter, string EventType, SsfDeliveryMethod Delivery)> Events { get; } = [];

    public List<(string? Transmitter, TimeSpan Duration, bool Success)> Polls { get; } = [];

    public void SetReceived(string? transmitter, SsfDeliveryMethod deliveryMethod, SsfSetOutcome outcome)
    {
        Sets.Add((transmitter, deliveryMethod, outcome));
    }

    public void EventHandled(string transmitter, string eventType, SsfDeliveryMethod deliveryMethod)
    {
        Events.Add((transmitter, eventType, deliveryMethod));
    }

    public void PollCompleted(string? transmitter, TimeSpan duration, bool success)
    {
        Polls.Add((transmitter, duration, success));
    }

    public void PollerStarted(string? transmitter, Func<int> pendingAcks)
    {
    }
}
