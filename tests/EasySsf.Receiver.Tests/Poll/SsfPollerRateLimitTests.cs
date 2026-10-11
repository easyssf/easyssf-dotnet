using System.Net;

using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Tests.Set;
using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Tests.Poll;

/// <summary>
/// How the poller reacts when the transmitter rate-limits the poll endpoint.
/// </summary>
public sealed class SsfPollerRateLimitTests : IDisposable
{
    private readonly ScriptedHttpMessageHandler _http = new();

    private readonly HttpClient _client;

    private readonly SsfPollerOptions _options = new();

    private readonly SsfPoller _poller;

    public SsfPollerRateLimitTests()
    {
        _client = new HttpClient(_http);
        SsfSetProcessor processor = new(new DelegateVerifier(_ => throw new InvalidOperationException("no SET expected")), null, []);
        _poller = new SsfPoller(_client, SsfTransmitterTokenProviders.Fixed("token"), () => new Uri("https://tr.example/poll"), processor, _options);
    }

    public void Dispose()
    {
        _poller.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task PausesForRetryAfter()
    {
        _http.Respond(HttpStatusCode.TooManyRequests, new Dictionary<string, string> { ["Retry-After"] = "120" }, "");
        SsfPollException ex = await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Equal(429, ex.StatusCode);
        Assert.InRange(_poller.PausedUntil, DateTimeOffset.UtcNow.AddSeconds(110), DateTimeOffset.UtcNow.AddSeconds(121));
        // paused: the next poll makes no request
        Assert.Equal(0, await _poller.PollNowAsync());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task PollsAgainAtTheRegularIntervalAfter429WithoutRetryAfterByDefault()
    {
        _http.Respond(HttpStatusCode.TooManyRequests, "").Respond(HttpStatusCode.TooManyRequests, "");
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.True(_poller.PausedUntil < DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task PausesForTheFallbackAfter429WithoutRetryAfter()
    {
        _options.RateLimitFallback = TimeSpan.FromSeconds(45);
        _http.Respond(HttpStatusCode.TooManyRequests, "");
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.InRange(_poller.PausedUntil, DateTimeOffset.UtcNow.AddSeconds(40), DateTimeOffset.UtcNow.AddSeconds(46));
        Assert.Equal(0, await _poller.PollNowAsync());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task FallbackDoesNotApplyTo503WithoutRetryAfter()
    {
        _options.RateLimitFallback = TimeSpan.FromSeconds(45);
        _http.Respond(HttpStatusCode.ServiceUnavailable, "");
        SsfPollException ex = await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.Equal(503, ex.StatusCode);
        Assert.True(_poller.PausedUntil < DateTimeOffset.UtcNow);
        Assert.Contains("503", _poller.LastPollError);
    }

    [Fact]
    public async Task PauseIsCappedAtTheMaximum()
    {
        _options.MaxPause = TimeSpan.FromSeconds(60);
        _http.Respond(HttpStatusCode.ServiceUnavailable, new Dictionary<string, string> { ["Retry-After"] = "3600" }, "");
        await Assert.ThrowsAsync<SsfPollException>(() => _poller.PollNowAsync());
        Assert.InRange(_poller.PausedUntil, DateTimeOffset.UtcNow.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(61));
        // still paused from before, so no request is made
        _options.RateLimitFallback = TimeSpan.FromHours(1);
        _http.Respond(HttpStatusCode.TooManyRequests, "");
        _options.MaxPause = TimeSpan.FromSeconds(1);
        Assert.Equal(0, await _poller.PollNowAsync());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ReportsAnUnreachableTransmitter()
    {
        using HttpClient failing = new(new FailingHandler());
        using SsfPoller poller = new(failing, SsfTransmitterTokenProviders.None, () => new Uri("https://tr.example/poll"),
            new SsfSetProcessor(new DelegateVerifier(_ => throw new InvalidOperationException()), null, []));
        SsfPollException ex = await Assert.ThrowsAsync<SsfPollException>(() => poller.PollNowAsync());
        Assert.Equal(0, ex.StatusCode);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }
}
