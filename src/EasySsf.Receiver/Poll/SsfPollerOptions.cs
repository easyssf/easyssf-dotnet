using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Poll;

/// <summary>
/// How an <see cref="SsfPoller"/> polls. Read when the poller works, so configure the options before
/// <see cref="SsfPoller.Start"/>.
/// </summary>
public sealed class SsfPollerOptions
{
    /// <summary>
    /// Keeps the acknowledgements until a poll request carried them; an
    /// <see cref="InMemorySsfPollAckStore"/> by default.
    /// </summary>
    public ISsfPollAckStore AckStore { get; set; } = new InMemorySsfPollAckStore();

    /// <summary>
    /// The most acknowledgements and error reports one request carries, the rest goes with the next
    /// one; 1000 by default.
    /// </summary>
    public int MaxAckBatch { get; set; } = 1000;

    /// <summary>
    /// Records the polls. Records nothing by default.
    /// </summary>
    public ISsfReceiverMetrics Metrics { get; set; } = NullSsfReceiverMetrics.Instance;

    /// <summary>
    /// The time between two polls, 30 seconds by default. With long polling, the pause after a
    /// failed request or after a transmitter answered an empty long poll at once.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="SsfPoller.Start"/> waits before the first poll, nothing by default.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How soon the poller looks again while the poll endpoint of the stream is not known yet, one
    /// second by default; never longer than the interval.
    /// </summary>
    public TimeSpan EndpointRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Switches to long polling (RFC 8936, section 2.5) when set: the first request of a poll asks
    /// the transmitter to hold it until SETs are available (<c>returnImmediately: false</c>), and a
    /// running poller sends the next request as soon as a response was handled. The value is how
    /// long the transmitter holds a request, part of the agreement with it (section 2.2); the
    /// request waits that long plus a margin for the response, so the <c>HttpClient</c> timeout
    /// has to allow for it. <see langword="null"/>, the default, is short polling.
    /// </summary>
    public TimeSpan? LongPollingHold { get; set; }

    /// <summary>
    /// Whether <see cref="SsfPoller.StopAsync"/> sends the pending acknowledgements with a last
    /// request, so that the transmitter does not deliver the SETs again after a restart;
    /// <see langword="true"/> by default.
    /// </summary>
    public bool FlushOnStop { get; set; } = true;

    /// <summary>
    /// The maximum number of SETs fetched with one request, 100 by default.
    /// </summary>
    public int MaxEvents { get; set; } = 100;

    /// <summary>
    /// The issuer of the transmitter that is polled, for the metrics and as the key of its
    /// acknowledgements in the <see cref="AckStore"/>; the poll endpoint if <see langword="null"/>.
    /// </summary>
    public string? Transmitter { get; set; }

    /// <summary>
    /// How long to pause polling after a <c>429 Too Many Requests</c> without a <c>Retry-After</c>
    /// header; <see langword="null"/>, the default, to poll again at the regular interval.
    /// </summary>
    public TimeSpan? RateLimitFallback { get; set; }

    /// <summary>
    /// The longest pause a <c>Retry-After</c> header or the rate limit fallback can cause, 5 minutes
    /// by default.
    /// </summary>
    public TimeSpan MaxPause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What the poll endpoint must look like before the access token is sent to it,
    /// <see cref="SsfTransmitterUriPolicy.Default"/> unless set: <c>https</c>, or <c>http</c> on
    /// loopback addresses only. <see cref="SsfTransmitterUriPolicy.Insecure"/> for a transmitter
    /// without TLS in a test setup.
    /// </summary>
    public SsfTransmitterUriPolicy UriPolicy { get; set; } = SsfTransmitterUriPolicy.Default;

    /// <summary>
    /// The clock the poller paces itself by.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(AckStore, nameof(AckStore));
        ArgumentNullException.ThrowIfNull(Metrics, nameof(Metrics));
        ArgumentNullException.ThrowIfNull(TimeProvider, nameof(TimeProvider));
        ArgumentNullException.ThrowIfNull(UriPolicy, nameof(UriPolicy));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxAckBatch, 0, nameof(MaxAckBatch));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxEvents, 0, nameof(MaxEvents));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero, nameof(Interval));
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialDelay, TimeSpan.Zero, nameof(InitialDelay));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(EndpointRetry, TimeSpan.Zero, nameof(EndpointRetry));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxPause, TimeSpan.Zero, nameof(MaxPause));
        if (LongPollingHold is { } hold)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hold, TimeSpan.Zero, nameof(LongPollingHold));
        }
        if (RateLimitFallback is { } fallback)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(fallback, TimeSpan.Zero, nameof(RateLimitFallback));
        }
    }
}
