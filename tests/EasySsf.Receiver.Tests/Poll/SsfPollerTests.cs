using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Tests.Set;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Poll;

public sealed class SsfPollerTests : IClassFixture<TestTransmitter>, IDisposable
{
    private readonly TestTransmitter _transmitter;

    private readonly HttpClient _http;

    private readonly List<string> _handled = [];

    private readonly Queue<Exception> _failures = new();

    /// <summary>runs after a SET was handled, before it is acknowledged</summary>
    private Action _afterHandling = () => { };

    private readonly RecordingMetrics _metrics = new();

    private readonly List<SsfPoller> _pollers = [];

    private readonly SsfPoller _poller;

    public SsfPollerTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _transmitter.Reset();
        _http = transmitter.CreateHttpClient();
        _poller = Poller(() => new Uri(transmitter.PollUri), TestTransmitter.DefaultAccessToken);
    }

    public void Dispose()
    {
        foreach (SsfPoller poller in _pollers)
        {
            poller.StopAsync().GetAwaiter().GetResult();
            poller.Dispose();
        }
        _http.Dispose();
    }

    private SsfPoller Poller(Func<Uri?> endpoint, string accessToken, Action<SsfPollerOptions>? configure = null)
    {
        return Poller(endpoint, SsfTransmitterTokenProviders.Fixed(accessToken), configure);
    }

    private SsfPoller Poller(Func<Uri?> endpoint, ISsfTransmitterTokenProvider tokenProvider, Action<SsfPollerOptions>? configure = null)
    {
        ISsfEventHandler handler = SsfEventHandler.Of((eventContext, _) =>
        {
            if (_failures.TryDequeue(out Exception? failure))
            {
                throw failure;
            }
            _handled.Add(eventContext.EventToken.Jti);
            _afterHandling();
            return Task.CompletedTask;
        });
        JwtSsfSetVerifier verifier = new(_transmitter.Issuer, new Uri(_transmitter.JwksUri), _http);
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(100), [handler], new SsfSetProcessorOptions { Metrics = _metrics });
        SsfPollerOptions options = new() { Metrics = _metrics };
        configure?.Invoke(options);
        SsfPoller poller = new(_http, tokenProvider, endpoint, processor, options);
        _pollers.Add(poller);
        return poller;
    }

    [Fact]
    public async Task StartIsIdempotentAndStopEndsThePolling()
    {
        _poller.Options.InitialDelay = TimeSpan.FromHours(1);
        _poller.Start();
        _poller.Start();
        Assert.True(_poller.IsRunning);
        await _poller.StopAsync();
        await _poller.StopAsync();
        Assert.False(_poller.IsRunning);
        Assert.Equal(0, _transmitter.PollRequests);
    }

    [Fact]
    public async Task HandlesAndAcknowledgesAvailableSets()
    {
        string first = Queue("session-1");
        string second = Queue("session-2");

        Assert.Equal(2, await _poller.PollNowAsync());

        Assert.Equal([first, second], _handled);
        Assert.Equal([first, second], _transmitter.AcknowledgedSets);
        Assert.Equal(0, await _poller.PollNowAsync());
        Assert.Equal(2, _metrics.Sets.Count(s => s.Delivery == SsfDeliveryMethod.Poll && s.Outcome == SsfSetOutcome.Handled));
        Assert.Equal(2, _metrics.Events.Count(e => e.Delivery == SsfDeliveryMethod.Poll && e.EventType == SsfEventTypes.CaepSessionRevoked));
        Assert.True(_metrics.Polls.Count(p => p.Success) >= 2);
        Assert.NotNull(_poller.LastPollAt);
        Assert.NotNull(_poller.LastSuccessfulPollAt);
        Assert.Null(_poller.LastPollError);
    }

    [Fact]
    public async Task FetchesAllSetsWhenMoreAreAvailableThanFitInOneRequest()
    {
        List<string> queued = [Queue("session-1"), Queue("session-2"), Queue("session-3")];
        _poller.Options.MaxEvents = 1;

        Assert.Equal(3, await _poller.PollNowAsync());

        Assert.Equal(queued, _handled);
        Assert.Equal(queued, _transmitter.AcknowledgedSets);
    }

    [Fact]
    public async Task ReportsInvalidSetToTheTransmitter()
    {
        string valid = Queue("session-1");
        _transmitter.QueueSet("broken", "not-a-set");

        Assert.Equal(2, await _poller.PollNowAsync());

        Assert.Equal([valid], _handled);
        Assert.Equal([valid], _transmitter.AcknowledgedSets);
        Assert.Equal(["broken"], _transmitter.ReportedErrors.Keys);
        Assert.Equal("invalid_request", _transmitter.ReportedErrors["broken"]!["err"]!.GetValue<string>());
        Assert.Equal(1, _metrics.Sets.Count(s => s.Delivery == SsfDeliveryMethod.Poll && s.Outcome == SsfSetOutcome.Invalid));
    }

    [Fact]
    public async Task DoesNotAcknowledgeSetThatCouldNotBeHandledSoThatItIsDeliveredAgain()
    {
        string jti = Queue("session-1");
        _failures.Enqueue(new InvalidOperationException("store is down"));

        Assert.Equal(1, await _poller.PollNowAsync());
        Assert.Empty(_handled);
        Assert.Empty(_transmitter.AcknowledgedSets);

        Assert.Equal(1, await _poller.PollNowAsync());
        Assert.Equal([jti], _handled);
        Assert.Equal([jti], _transmitter.AcknowledgedSets);
    }

    [Fact]
    public async Task ObtainsNewAccessTokenWhenTheTransmitterRejectsTheCurrentOne()
    {
        using ClientCredentialsSsfTransmitterTokenProvider tokenProvider = new(_http, new Uri(_transmitter.TokenUri),
            TestTransmitter.ClientId, TestTransmitter.ClientSecret);
        SsfPoller poller = Poller(() => new Uri(_transmitter.PollUri), tokenProvider);
        Assert.Equal(0, await poller.PollNowAsync());
        string jti = Queue("session-1");

        _transmitter.ExpireAccessTokens();

        Assert.Equal(1, await poller.PollNowAsync());
        Assert.Equal([jti], _transmitter.AcknowledgedSets);
    }

    [Fact]
    public async Task DoesNotPollWhileThePollEndpointIsUnknown()
    {
        Queue("session-1");
        Assert.Equal(0, await Poller(() => null, TestTransmitter.DefaultAccessToken).PollNowAsync());
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task PollsSoonAfterTheEndpointBecomesKnownInsteadOfWaitingTheInterval()
    {
        Uri? endpoint = null;
        SsfPoller waiting = Poller(() => endpoint, TestTransmitter.DefaultAccessToken, options =>
        {
            options.Interval = TimeSpan.FromHours(1);
            options.EndpointRetry = TimeSpan.FromMilliseconds(100);
        });
        string jti = Queue("session-1");
        waiting.Start();
        try
        {
            // the stream is still being registered: nothing is polled
            await Eventually.DuringAsync(() => _handled.Count == 0, TimeSpan.FromMilliseconds(300));
            // registered: the poller notices within the retry, not after the interval
            endpoint = new Uri(_transmitter.PollUri);
            await Eventually.UntilAssertedAsync(() => Assert.Equal([jti], _handled));
        }
        finally
        {
            await waiting.StopAsync();
        }
    }

    [Fact]
    public async Task WakeUpEndsThePauseAndPollsAtOnce()
    {
        _poller.Options.Interval = TimeSpan.FromHours(1);
        _poller.Start();
        try
        {
            await Eventually.UntilAsync(() => _transmitter.PollRequests == 1);
            string jti = Queue("session-1");
            await Eventually.DuringAsync(() => _handled.Count == 0, TimeSpan.FromMilliseconds(300));
            _poller.WakeUp();
            await Eventually.UntilAssertedAsync(() => Assert.Equal([jti], _handled));
        }
        finally
        {
            await _poller.StopAsync();
        }
    }

    [Fact]
    public async Task WakeUpDoesNothingWhileThePollerIsNotRunning()
    {
        Queue("session-1");
        _poller.WakeUp();
        await Eventually.DuringAsync(() => _transmitter.PollRequests == 0, TimeSpan.FromMilliseconds(300));
        Assert.Empty(_handled);
    }

    [Fact]
    public async Task ReportsFailedPollRequest()
    {
        SsfPoller unauthenticated = Poller(() => new Uri(_transmitter.PollUri), "wrong-token");
        SsfPollException ex = await Assert.ThrowsAsync<SsfPollException>(() => unauthenticated.PollNowAsync());
        Assert.Contains("401", ex.Message);
        Assert.Equal(1, _metrics.Polls.Count(p => !p.Success));
        Assert.Contains("401", unauthenticated.LastPollError);
        Assert.NotNull(unauthenticated.LastPollAt);
        Assert.Null(unauthenticated.LastSuccessfulPollAt);
    }

    [Fact]
    public async Task AcknowledgementsWaitInTheStoreUntilARequestCarriedThem()
    {
        InMemorySsfPollAckStore store = new();
        _poller.Options.AckStore = store;
        _poller.Options.Transmitter = _transmitter.Issuer;
        string jti = Queue("session-1");
        _transmitter.QueueSet("broken", "not-a-set");
        // the transmitter goes away between handling the SETs and the request that acknowledges them
        _afterHandling = () => _transmitter.Available = false;

        SsfPollException ex = await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Contains("503", ex.Message);

        Assert.Equal([jti], _handled);
        Assert.Empty(_transmitter.AcknowledgedSets);
        Assert.Equal([jti, "broken"], (await store.PendingAsync(_transmitter.Issuer, 10)).Select(ack => ack.Jti));
        Assert.Equal(2, _poller.PendingAckCount);
        Assert.Equal(2, await _poller.GetPendingAckCountAsync());

        _transmitter.Available = true;
        _afterHandling = () => { };
        await _poller.PollNowAsync();
        Assert.Equal([jti], _transmitter.AcknowledgedSets);
        Assert.Equal(["broken"], _transmitter.ReportedErrors.Keys);
        Assert.Equal(0, await store.CountAsync(_transmitter.Issuer));
        Assert.Equal(0, _poller.PendingAckCount);
    }

    [Fact]
    public async Task AcknowledgementsAreSentInBatches()
    {
        _poller.Options.MaxAckBatch = 2;
        List<string> queued = [Queue("session-1"), Queue("session-2"), Queue("session-3")];

        Assert.Equal(3, await _poller.PollNowAsync());
        // the request after handling carried two, the third waits for the next poll
        Assert.Equal([queued[0], queued[1]], _transmitter.AcknowledgedSets);
        Assert.Equal(1, _poller.PendingAckCount);

        await _poller.PollNowAsync();
        Assert.Equal(queued, _transmitter.AcknowledgedSets);
        Assert.Equal(0, _poller.PendingAckCount);
    }

    [Fact]
    public async Task PendingAcknowledgementsAreAGauge()
    {
        _poller.Options.Transmitter = _transmitter.Issuer;
        _poller.Options.InitialDelay = TimeSpan.FromHours(1);
        _poller.Start();
        await _poller.StopAsync();
        Func<int> gauge = _metrics.PendingAcks[_transmitter.Issuer];
        Assert.Equal(0, gauge());
        string jti = Queue("session-1");
        _afterHandling = () => _transmitter.Available = false;
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Equal([jti], _handled);
        Assert.Equal(1, gauge());
    }

    [Fact]
    public async Task StopFlushesThePendingAcknowledgements()
    {
        _poller.Options.InitialDelay = TimeSpan.FromHours(1);
        string jti = Queue("session-1");
        _afterHandling = () => _transmitter.Available = false;
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Equal(1, _poller.PendingAckCount);
        _transmitter.Available = true;

        _poller.Start();
        await _poller.StopAsync();

        Assert.Equal([jti], _transmitter.AcknowledgedSets);
        Assert.Equal(0, _poller.PendingAckCount);
    }

    [Fact]
    public async Task LongPollIsHeldUntilASetArrives()
    {
        _transmitter.LongPollHold = TimeSpan.FromSeconds(5);
        _poller.Options.LongPollingHold = TimeSpan.FromSeconds(5);
        _poller.Options.Interval = TimeSpan.FromSeconds(30);
        Assert.True(_poller.IsLongPolling);
        _poller.Start();
        try
        {
            await Eventually.UntilAsync(() => _transmitter.PollRequests >= 1);
            Assert.False(_transmitter.LastPollRequest["returnImmediately"]!.GetValue<bool>());
            Assert.Empty(_handled);

            DateTimeOffset queuedAt = DateTimeOffset.UtcNow;
            string jti = Queue("session-1");
            await Eventually.UntilAssertedAsync(() => Assert.Equal([jti], _handled), TimeSpan.FromSeconds(3));
            // handled while the request was held, long before the hold time elapsed
            Assert.True(DateTimeOffset.UtcNow - queuedAt < TimeSpan.FromSeconds(3));
            // the next request went out right away and carries the acknowledgement
            await Eventually.UntilAssertedAsync(() => Assert.Equal([jti], _transmitter.AcknowledgedSets), TimeSpan.FromSeconds(3));
            Assert.False(_transmitter.LastPollRequest["returnImmediately"]!.GetValue<bool>());
        }
        finally
        {
            await _poller.StopAsync();
        }
    }

    [Fact]
    public async Task LongPollAgainstATransmitterThatAnswersAtOnceWaitsTheInterval()
    {
        // the test transmitter does not hold by default
        _poller.Options.LongPollingHold = TimeSpan.FromSeconds(5);
        _poller.Options.Interval = TimeSpan.FromMinutes(5);
        _poller.Start();
        try
        {
            await Eventually.UntilAsync(() => _transmitter.PollRequests >= 1);
            await Eventually.DuringAsync(() => _transmitter.PollRequests == 1, TimeSpan.FromSeconds(1));
        }
        finally
        {
            await _poller.StopAsync();
        }
    }

    [Fact]
    public async Task StopInterruptsAHeldLongPoll()
    {
        _transmitter.LongPollHold = TimeSpan.FromSeconds(30);
        _poller.Options.LongPollingHold = TimeSpan.FromSeconds(30);
        _poller.Start();
        await Eventually.UntilAsync(() => _transmitter.PollRequests >= 1);
        DateTimeOffset stoppedAt = DateTimeOffset.UtcNow;
        await _poller.StopAsync();
        Assert.True(DateTimeOffset.UtcNow - stoppedAt < TimeSpan.FromSeconds(5));
        Assert.False(_poller.IsRunning);
    }

    [Fact]
    public async Task PollNowAsksForAnImmediateAnswerEvenWhenLongPolling()
    {
        _transmitter.LongPollHold = TimeSpan.FromSeconds(30);
        _poller.Options.LongPollingHold = TimeSpan.FromSeconds(30);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Assert.Equal(0, await _poller.PollNowAsync());
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(_transmitter.LastPollRequest["returnImmediately"]!.GetValue<bool>());
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Poller(() => null, "t", options => options.Interval = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Poller(() => null, "t", options => options.MaxEvents = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Poller(() => null, "t", options => options.LongPollingHold = TimeSpan.Zero));
    }

    private string Queue(string sessionId)
    {
        JsonObject claims = _transmitter.SetClaims("CaepSessionRevoked", Opaque(sessionId));
        _transmitter.QueueSet(_transmitter.SignSet(claims));
        return claims["jti"]!.GetValue<string>();
    }
}
