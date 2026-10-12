using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Poll;

/// <summary>
/// Fetches SETs from the poll endpoint of the transmitter (RFC 8936).
/// <para>
/// A SET is acknowledged once it was handled. An invalid SET is reported as an error to the
/// transmitter. A SET that could not be handled is neither acknowledged nor reported, the
/// transmitter delivers it again with a later poll. Acknowledgements and error reports ride on the
/// next poll request (section 2.4) and wait in the <see cref="ISsfPollAckStore"/> until that
/// request succeeded; with the in-memory store of the default they are lost when the application
/// stops, with a durable store they survive a restart.
/// </para>
/// <para>
/// By default the poller polls at a fixed <see cref="SsfPollerOptions.Interval"/> and asks the
/// transmitter to answer right away (<c>returnImmediately: true</c>, short polling). With
/// <see cref="SsfPollerOptions.LongPollingHold">long polling</see> it keeps one request outstanding
/// instead: the transmitter holds the request until SETs are available or its hold time elapses
/// (section 2.5), and the poller sends the next request as soon as it has handled the response. A
/// backlog is fetched with immediate requests in both modes.
/// </para>
/// <para>
/// Until the poll endpoint of the stream is known, typically because the
/// <c>SsfStreamRegistrar</c> is still registering the stream when the poller starts, the poller
/// looks again every <see cref="SsfPollerOptions.EndpointRetry"/> rather than waiting the interval:
/// not knowing the endpoint is a condition of the startup, not a request of the transmitter to slow
/// down. <see cref="WakeUp()"/> ends the pause between two polls altogether; <c>SsfTransmitter</c>
/// calls it when the stream is registered, so that the first poll follows the registration at once.
/// </para>
/// </summary>
public sealed class SsfPoller : IDisposable
{
    private const int MaxRequestsPerPoll = 100;

    /// <summary>
    /// How much longer than the hold time of the transmitter a long poll waits for the response.
    /// </summary>
    private static readonly TimeSpan LongPollMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A long poll that comes back empty faster than this was not held: the transmitter does not
    /// support long polling, and the poller waits the interval before the next request instead of
    /// asking again at once.
    /// </summary>
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(1);

    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly ISsfTransmitterTokenProvider _tokenProvider;

    private readonly Func<Uri?> _endpoint;

    private readonly SsfSetProcessor _processor;

    private readonly SsfPollerOptions _options;

    private readonly ILogger _logger;

    /// <summary>one poll at a time</summary>
    private readonly SemaphoreSlim _polling = new(1, 1);

    /// <summary>Start() and StopAsync() are idempotent and safe to call from any thread</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private readonly Lock _pausing = new();

    private TaskCompletionSource _wokenUp = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _wakeUpRequested;

    private CancellationTokenSource? _stopping;

    private Task? _loop;

    private volatile bool _running;

    private long _pausedUntilTicks = DateTimeOffset.MinValue.UtcTicks;

    private long _lastPollAtTicks;

    private long _lastSuccessfulPollAtTicks;

    private volatile string? _lastPollError;

    private volatile int _pendingAckCount;

    /// <param name="httpClient">used to call the transmitter</param>
    /// <param name="tokenProvider">provides the access token to authenticate with</param>
    /// <param name="endpoint">supplies the poll endpoint, <see langword="null"/> as long as it is not known</param>
    /// <param name="processor">processes the fetched SETs</param>
    /// <param name="options">how to poll, the defaults if <see langword="null"/></param>
    /// <param name="logger">logs the polls, nothing if <see langword="null"/></param>
    public SsfPoller(HttpClient httpClient, ISsfTransmitterTokenProvider tokenProvider, Func<Uri?> endpoint, SsfSetProcessor processor,
            SsfPollerOptions? options = null, ILogger<SsfPoller>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(processor);
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _endpoint = endpoint;
        _processor = processor;
        _options = options ?? new SsfPollerOptions();
        _options.Validate();
        _logger = logger ?? NullLogger<SsfPoller>.Instance;
    }

    /// <summary>
    /// How the poller polls.
    /// </summary>
    public SsfPollerOptions Options => _options;

    /// <summary>
    /// Whether the poller long polls, see <see cref="SsfPollerOptions.LongPollingHold"/>.
    /// </summary>
    public bool IsLongPolling => _options.LongPollingHold is not null;

    /// <summary>
    /// Starts polling the transmitter in the background: periodically, or with one request always
    /// outstanding when long polling. Without it, SETs are only fetched by calling
    /// <see cref="PollNowAsync"/>. Calling it again while running has no effect.
    /// </summary>
    public void Start()
    {
        _lifecycle.Wait();
        try
        {
            if (_loop is not null)
            {
                return;
            }
            _options.Validate();
            _running = true;
            _options.Metrics.PollerStarted(_options.Transmitter, () => PendingAckCount);
            CancellationTokenSource stopping = new();
            _stopping = stopping;
            // the token is read here, not on the pool thread: StopAsync may dispose the source
            // before the task gets to run
            CancellationToken token = stopping.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops polling: ends the polling loop, cancelling an outstanding long poll, and sends the
    /// pending acknowledgements with a last request unless <see cref="SsfPollerOptions.FlushOnStop"/>
    /// says otherwise.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task? loop = _loop;
            CancellationTokenSource? stopping = _stopping;
            _loop = null;
            _stopping = null;
            _running = false;
            if (loop is null)
            {
                return;
            }
            WakeUp(force: true);
            if (stopping is not null)
            {
                await stopping.CancelAsync().ConfigureAwait(false);
            }
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("The SSF poller did not end within 5 seconds");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // the loop was cancelled
            }
            stopping?.Dispose();
            if (_options.FlushOnStop)
            {
                await FlushAcksAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Whether <see cref="Start"/> was called and <see cref="StopAsync"/> was not.
    /// </summary>
    public bool IsRunning => _loop is not null;

    /// <summary>
    /// Ends the pause between two polls, so that the next poll runs now, in the background and with
    /// the poller's error handling. Does nothing while the poller is not running;
    /// <see cref="PollNowAsync"/> polls on the caller's behalf regardless.
    /// </summary>
    public void WakeUp()
    {
        WakeUp(force: false);
    }

    private void WakeUp(bool force)
    {
        lock (_pausing)
        {
            if (force || _loop is not null)
            {
                _wakeUpRequested = true;
                _wokenUp.TrySetResult();
            }
        }
    }

    /// <summary>
    /// When the transmitter was last polled, <see langword="null"/> if it was not polled yet.
    /// </summary>
    public DateTimeOffset? LastPollAt => Instant(Volatile.Read(ref _lastPollAtTicks));

    /// <summary>
    /// When the transmitter was last polled successfully, <see langword="null"/> if never.
    /// </summary>
    public DateTimeOffset? LastSuccessfulPollAt => Instant(Volatile.Read(ref _lastSuccessfulPollAtTicks));

    /// <summary>
    /// Why the last poll failed, <see langword="null"/> if it succeeded or none was made yet.
    /// </summary>
    public string? LastPollError => _lastPollError;

    /// <summary>
    /// Until when the transmitter asked not to be polled, in the past if it did not.
    /// </summary>
    public DateTimeOffset PausedUntil => new(Volatile.Read(ref _pausedUntilTicks), TimeSpan.Zero);

    /// <summary>
    /// The number of acknowledgements and error reports waiting for the next poll request, as of
    /// the last time the store was read or written.
    /// </summary>
    public int PendingAckCount => _pendingAckCount;

    /// <summary>
    /// Reads the number of acknowledgements and error reports waiting for the next poll request
    /// from the store.
    /// </summary>
    public async ValueTask<int> GetPendingAckCountAsync(CancellationToken cancellationToken = default)
    {
        string? key = AckKey(_endpoint());
        if (key is null)
        {
            return 0;
        }
        int count = await _options.AckStore.CountAsync(key, cancellationToken).ConfigureAwait(false);
        _pendingAckCount = count;
        return count;
    }

    private static DateTimeOffset? Instant(long ticks)
    {
        return (ticks == 0) ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_options.InitialDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.InitialDelay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            while (_running && !cancellationToken.IsCancellationRequested)
            {
                ClearWakeUp();
                TimeSpan pause = _options.Interval;
                long started = _options.TimeProvider.GetTimestamp();
                try
                {
                    PollResult result = _polling.Wait(0, CancellationToken.None)
                        ? await PollLockedAsync(cancellationToken).ConfigureAwait(false) : PollResult.Skipped;
                    if (result.EndpointUnknown)
                    {
                        // the stream is still being registered: look again soon
                        pause = (_options.EndpointRetry < _options.Interval) ? _options.EndpointRetry : _options.Interval;
                    }
                    else if (result.IsSkipped)
                    {
                        pause = PauseWhileSkipped();
                    }
                    else if (IsLongPolling)
                    {
                        TimeSpan elapsed = _options.TimeProvider.GetElapsedTime(started);
                        bool held = result.Fetched > 0 || elapsed >= MinHold;
                        pause = held ? TimeSpan.Zero : _options.Interval;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!_running)
                    {
                        return;
                    }
                    _logger.LogWarning("Could not poll the SSF transmitter: {Reason}", ex.Message);
                    _logger.LogDebug(ex, "Cause of the failed poll");
                }
                if (_running && pause > TimeSpan.Zero)
                {
                    await PauseAsync(pause, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync() ends the loop
        }
    }

    /// <summary>
    /// Waits the pause, unless <see cref="WakeUp()"/> ends it earlier.
    /// </summary>
    private async Task PauseAsync(TimeSpan pause, CancellationToken cancellationToken)
    {
        Task wokenUp;
        lock (_pausing)
        {
            if (_wakeUpRequested || !_running)
            {
                return;
            }
            wokenUp = _wokenUp.Task;
        }
        using CancellationTokenSource pauseEnded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(pause, _options.TimeProvider, pauseEnded.Token);
        await Task.WhenAny(delay, wokenUp).ConfigureAwait(false);
        await pauseEnded.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A wake-up during a poll is not kept: a registration that completes meanwhile is picked up by
    /// the endpoint retry.
    /// </summary>
    private void ClearWakeUp()
    {
        lock (_pausing)
        {
            _wakeUpRequested = false;
            if (_wokenUp.Task.IsCompleted)
            {
                _wokenUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private async Task<PollResult> PollLockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await PollAsync(IsLongPolling, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _polling.Release();
        }
    }

    private TimeSpan PauseWhileSkipped()
    {
        TimeSpan untilResume = PausedUntil - Now;
        return (untilResume > TimeSpan.Zero && untilResume > _options.Interval) ? untilResume : _options.Interval;
    }

    /// <summary>
    /// Fetches and processes the SETs that are available at the transmitter and acknowledges them,
    /// with the request answered immediately whether or not long polling is on. One poll runs at a
    /// time: a call while another is in progress returns right away.
    /// </summary>
    /// <returns>
    /// the number of SETs fetched, <c>0</c> as well if the poll endpoint is not known yet, the
    /// transmitter asked to slow down or a poll is in progress
    /// </returns>
    /// <exception cref="SsfPollException">if the poll request failed</exception>
    public async Task<int> PollNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_polling.Wait(0, CancellationToken.None))
        {
            _logger.LogDebug("Not polling, a poll is in progress");
            return 0;
        }
        try
        {
            return (await PollAsync(false, cancellationToken).ConfigureAwait(false)).Fetched;
        }
        finally
        {
            _polling.Release();
        }
    }

    private async Task<PollResult> PollAsync(bool hold, CancellationToken cancellationToken)
    {
        Uri? endpoint = _endpoint();
        if (endpoint is null)
        {
            _logger.LogDebug("Not polling, the poll endpoint of the SSF stream is not known yet");
            return PollResult.NoEndpoint;
        }
        if (Now < PausedUntil)
        {
            _logger.LogDebug("Not polling, the SSF transmitter asked to wait until {PausedUntil}", PausedUntil);
            return PollResult.Skipped;
        }
        Volatile.Write(ref _lastPollAtTicks, Now.UtcTicks);
        string key = AckKey(endpoint)!;
        try
        {
            int fetched = 0;
            bool moreAvailable = true;
            for (int request = 0; moreAvailable && request < MaxRequestsPerPoll; request++)
            {
                // only the first request of a poll is held, a backlog is fetched right away
                JsonElement response = await PollAsync(endpoint, key, _options.MaxEvents, !(hold && request == 0), cancellationToken).ConfigureAwait(false);
                int processed = await ProcessSetsAsync(key, response, cancellationToken).ConfigureAwait(false);
                fetched += processed;
                moreAvailable = response.Member("moreAvailable") is { ValueKind: JsonValueKind.True } && processed > 0;
            }
            if (!hold && await GetPendingAckCountAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                // acknowledge right away instead of with the next poll; a long poll sends its next
                // request at once anyway
                JsonElement response = await PollAsync(endpoint, key, 0, true, cancellationToken).ConfigureAwait(false);
                fetched += await ProcessSetsAsync(key, response, cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _lastSuccessfulPollAtTicks, Now.UtcTicks);
            _lastPollError = null;
            return new PollResult(fetched, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastPollError = ex.Message;
            throw;
        }
    }

    private async Task<int> ProcessSetsAsync(string key, JsonElement response, CancellationToken cancellationToken)
    {
        if (response.Member("sets") is not { ValueKind: JsonValueKind.Object } sets)
        {
            return 0;
        }
        int count = 0;
        foreach (JsonProperty set in sets.EnumerateObject())
        {
            string encodedSet = (set.Value.ValueKind == JsonValueKind.String) ? set.Value.GetString()! : set.Value.GetRawText();
            await ProcessAsync(key, set.Name, encodedSet, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private async Task ProcessAsync(string key, string jti, string encodedSet, CancellationToken cancellationToken)
    {
        try
        {
            await _processor.ProcessAsync(encodedSet, SsfDeliveryMethod.Poll, cancellationToken).ConfigureAwait(false);
            await RecordAsync(key, SsfPendingAck.Ack(jti), cancellationToken).ConfigureAwait(false);
        }
        catch (SsfSetVerificationException ex)
        {
            _logger.LogWarning("Rejecting polled SET {Jti}: {Reason}", jti, ex.Message);
            await RecordAsync(key, SsfPendingAck.Error(jti, ex.ErrorCode, ex.Message), cancellationToken).ConfigureAwait(false);
        }
        catch (SsfSetInProgressException ex)
        {
            // another instance is on it, the transmitter delivers the SET again
            _logger.LogDebug("Not acknowledging polled SET {Jti}: {Reason}", jti, ex.Message);
        }
        catch (Exception ex) when (ex is SsfTransmitterUnavailableException or SsfEventHandlingException)
        {
            // not acknowledged, the transmitter delivers the SET again
            _logger.LogWarning("Could not process polled SET {Jti}: {Reason}", jti, ex.Message);
        }
    }

    private async Task RecordAsync(string key, SsfPendingAck ack, CancellationToken cancellationToken)
    {
        await _options.AckStore.RecordAsync(key, ack, cancellationToken).ConfigureAwait(false);
        _pendingAckCount = await _options.AckStore.CountAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> PollAsync(Uri endpoint, string key, int maxEvents, bool returnImmediately, CancellationToken cancellationToken)
    {
        IReadOnlyList<SsfPendingAck> pending = await _options.AckStore.PendingAsync(key, _options.MaxAckBatch, cancellationToken).ConfigureAwait(false);
        JsonArray acks = [];
        JsonObject errors = [];
        foreach (SsfPendingAck ack in pending)
        {
            if (ack.IsError)
            {
                JsonObject error = new() { ["err"] = ack.ErrorCode };
                if (ack.ErrorDescription is not null)
                {
                    error["description"] = ack.ErrorDescription;
                }
                errors[ack.Jti] = error;
            }
            else
            {
                acks.Add((JsonNode)ack.Jti);
            }
        }
        JsonObject request = new() { ["maxEvents"] = maxEvents, ["returnImmediately"] = returnImmediately };
        if (acks.Count > 0)
        {
            request["ack"] = acks;
        }
        if (errors.Count > 0)
        {
            request["setErrs"] = errors;
        }
        string json = request.ToJsonString();
        long started = _options.TimeProvider.GetTimestamp();
        HttpResponseMessage httpResponse;
        JsonElement response;
        try
        {
            httpResponse = await SendAsync(endpoint, json, returnImmediately, cancellationToken).ConfigureAwait(false);
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                // the access token expired or was revoked: try once more with a new one
                httpResponse.Dispose();
                _tokenProvider.Invalidate();
                httpResponse = await SendAsync(endpoint, json, returnImmediately, cancellationToken).ConfigureAwait(false);
            }
            using (httpResponse)
            {
                string body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    _options.Metrics.PollCompleted(_options.Transmitter, _options.TimeProvider.GetElapsedTime(started), false);
                    PauseIfAsked(httpResponse);
                    throw new SsfPollException($"Poll request to {endpoint} failed with status {(int)httpResponse.StatusCode}",
                        (int)httpResponse.StatusCode, null);
                }
                response = string.IsNullOrWhiteSpace(body) ? SsfJson.EmptyObject : SsfJson.Parse(body);
            }
        }
        catch (SsfPollException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Metrics.PollCompleted(_options.Transmitter, _options.TimeProvider.GetElapsedTime(started), false);
            throw new SsfPollException($"Poll request to {endpoint} failed: {ex.Message}", 0, ex);
        }
        if (pending.Count > 0)
        {
            await _options.AckStore.RemoveAsync(key, pending.Select(ack => ack.Jti), cancellationToken).ConfigureAwait(false);
            _pendingAckCount = await _options.AckStore.CountAsync(key, cancellationToken).ConfigureAwait(false);
        }
        _options.Metrics.PollCompleted(_options.Transmitter, _options.TimeProvider.GetElapsedTime(started), true);
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri endpoint, string json, bool returnImmediately, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(JsonMediaType);
        string? accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        if (!returnImmediately && _options.LongPollingHold is { } hold)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(hold + LongPollMargin);
            try
            {
                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The long poll was not answered within " + (hold + LongPollMargin), ex);
            }
        }
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the pending acknowledgements with a request that asks for no SETs, best effort.
    /// </summary>
    private async Task FlushAcksAsync(CancellationToken cancellationToken)
    {
        Uri? endpoint = _endpoint();
        string? key = AckKey(endpoint);
        if (key is null || await GetPendingAckCountAsync(cancellationToken).ConfigureAwait(false) == 0 || !_polling.Wait(0, CancellationToken.None))
        {
            return;
        }
        try
        {
            await PollAsync(endpoint!, key, 0, true, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Acknowledged the pending SETs before stopping");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not acknowledge the pending SETs before stopping, they are sent with the next poll: {Reason}", ex.Message);
        }
        finally
        {
            _polling.Release();
        }
    }

    private string? AckKey(Uri? endpoint)
    {
        return _options.Transmitter ?? endpoint?.ToString();
    }

    private void PauseIfAsked(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        TimeSpan? pause = null;
        if ((status == 429 || status == 503) && response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            // a date instead of seconds is treated like no header
            pause = retryAfter;
        }
        if (pause is null && status == 429)
        {
            pause = _options.RateLimitFallback;
        }
        if (pause is { } value)
        {
            TimeSpan capped = (value < _options.MaxPause) ? value : _options.MaxPause;
            Volatile.Write(ref _pausedUntilTicks, (Now + capped).UtcTicks);
        }
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        _polling.Dispose();
        _lifecycle.Dispose();
    }

    private readonly record struct PollResult(int Fetched, bool IsSkipped, bool EndpointUnknown)
    {
        public static PollResult Skipped { get; } = new(0, true, false);

        public static PollResult NoEndpoint { get; } = new(0, true, true);
    }
}
