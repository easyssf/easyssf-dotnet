using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using EasySsf.Core;
using EasySsf.Core.Event;

namespace EasySsf.Receiver.Metrics;

/// <summary>
/// <see cref="ISsfReceiverMetrics"/> that records to a <see cref="Meter"/> named
/// <see cref="MeterName"/>:
/// <list type="bullet">
/// <item><c>easyssf.receiver.sets</c>: received SETs, tagged with <c>transmitter</c>,
/// <c>delivery</c> (<c>push</c>, <c>poll</c>) and <c>outcome</c> (<c>handled</c>,
/// <c>duplicate</c>, <c>in_progress</c>, <c>invalid</c>, <c>unauthenticated</c>,
/// <c>unavailable</c>, <c>failed</c>)</item>
/// <item><c>easyssf.receiver.events</c>: handled events, tagged with <c>transmitter</c>,
/// <c>delivery</c> and <c>event</c> (for example <c>CaepSessionRevoked</c>)</item>
/// <item><c>easyssf.receiver.poll</c>: the duration of poll requests in seconds, tagged with
/// <c>transmitter</c> and <c>outcome</c> (<c>success</c>, <c>failure</c>)</item>
/// <item><c>easyssf.receiver.poll.pending-acks</c>: acknowledgements and error reports a poller
/// owes its transmitter, tagged with <c>transmitter</c></item>
/// </list>
/// The same names as the Java library's Micrometer metrics.
/// </summary>
public sealed class MeterSsfReceiverMetrics : ISsfReceiverMetrics
{
    /// <summary>
    /// The name of the <see cref="Meter"/>, to listen to or export.
    /// </summary>
    public const string MeterName = "EasySsf.Receiver";

    private readonly Counter<long> _sets;

    private readonly Counter<long> _events;

    private readonly Histogram<double> _poll;

    private readonly ConcurrentDictionary<string, Func<int>> _pendingAcks = new(StringComparer.Ordinal);

    private readonly Func<string, string?> _transmitterLabels;

    /// <param name="meterFactory">creates the meter, as registered with <c>AddMetrics()</c></param>
    /// <param name="transmitterLabels">
    /// the value of the <c>transmitter</c> tag for the issuer of a transmitter, for example its
    /// name, as issuers are long; the issuer itself if <see langword="null"/>
    /// </param>
    public MeterSsfReceiverMetrics(IMeterFactory meterFactory, Func<string, string?>? transmitterLabels = null)
        : this(Create(meterFactory), transmitterLabels)
    {
    }

    /// <param name="meter">the meter to record to; the caller owns it</param>
    /// <param name="transmitterLabels">
    /// the value of the <c>transmitter</c> tag for the issuer of a transmitter, for example its
    /// name, as issuers are long; the issuer itself if <see langword="null"/>
    /// </param>
    public MeterSsfReceiverMetrics(Meter meter, Func<string, string?>? transmitterLabels = null)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _transmitterLabels = transmitterLabels ?? (issuer => issuer);
        _sets = meter.CreateCounter<long>("easyssf.receiver.sets", unit: "{set}",
            description: "Security event tokens received");
        _events = meter.CreateCounter<long>("easyssf.receiver.events", unit: "{event}",
            description: "Security events handled");
        _poll = meter.CreateHistogram<double>("easyssf.receiver.poll", unit: "s",
            description: "Poll requests to the transmitter");
        meter.CreateObservableGauge("easyssf.receiver.poll.pending-acks", ObservePendingAcks, unit: "{ack}",
            description: "Acknowledgements and error reports a poller owes its transmitter, waiting for the next poll request");
    }

    private static Meter Create(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        return meterFactory.Create(MeterName);
    }

    public void SetReceived(string? transmitter, SsfDeliveryMethod deliveryMethod, SsfSetOutcome outcome)
    {
        _sets.Add(1, new KeyValuePair<string, object?>("transmitter", Transmitter(transmitter)),
            new KeyValuePair<string, object?>("delivery", Tag(deliveryMethod)),
            new KeyValuePair<string, object?>("outcome", Tag(outcome)));
    }

    public void EventHandled(string transmitter, string eventType, SsfDeliveryMethod deliveryMethod)
    {
        _events.Add(1, new KeyValuePair<string, object?>("transmitter", Transmitter(transmitter)),
            new KeyValuePair<string, object?>("delivery", Tag(deliveryMethod)),
            new KeyValuePair<string, object?>("event", SsfEventTypes.AliasOf(eventType)));
    }

    public void PollCompleted(string? transmitter, TimeSpan duration, bool success)
    {
        _poll.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("transmitter", Transmitter(transmitter)),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
    }

    public void PollerStarted(string? transmitter, Func<int> pendingAcks)
    {
        ArgumentNullException.ThrowIfNull(pendingAcks);
        _pendingAcks[Transmitter(transmitter)] = pendingAcks;
    }

    private IEnumerable<Measurement<int>> ObservePendingAcks()
    {
        foreach ((string transmitter, Func<int> pendingAcks) in _pendingAcks)
        {
            yield return new Measurement<int>(pendingAcks(), new KeyValuePair<string, object?>("transmitter", transmitter));
        }
    }

    private string Transmitter(string? issuer)
    {
        string? label = (issuer is not null) ? _transmitterLabels(issuer) : null;
        return label ?? "unknown";
    }

    private static string Tag(SsfDeliveryMethod deliveryMethod)
    {
        return deliveryMethod switch
        {
            SsfDeliveryMethod.Push => "push",
            SsfDeliveryMethod.Poll => "poll",
            _ => deliveryMethod.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// The <c>outcome</c> tag of a SET: the lower-case, underscore separated name of the outcome,
    /// as the Java library records it.
    /// </summary>
    public static string Tag(SsfSetOutcome outcome)
    {
        return outcome switch
        {
            SsfSetOutcome.Handled => "handled",
            SsfSetOutcome.Duplicate => "duplicate",
            SsfSetOutcome.InProgress => "in_progress",
            SsfSetOutcome.Invalid => "invalid",
            SsfSetOutcome.Unauthenticated => "unauthenticated",
            SsfSetOutcome.Unavailable => "unavailable",
            SsfSetOutcome.Failed => "failed",
            _ => outcome.ToString().ToLowerInvariant(),
        };
    }
}
