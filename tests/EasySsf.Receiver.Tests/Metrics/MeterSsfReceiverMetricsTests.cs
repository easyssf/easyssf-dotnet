using System.Diagnostics.Metrics;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Metrics;

namespace EasySsf.Receiver.Tests.Metrics;

public sealed class MeterSsfReceiverMetricsTests : IDisposable
{
    private readonly Meter _meter = new(MeterSsfReceiverMetrics.MeterName);

    private readonly MeterListener _listener = new();

    private readonly List<(string Instrument, double Value, Dictionary<string, string> Tags)> _measurements = [];

    public MeterSsfReceiverMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _meter)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string> tagValues = new(StringComparer.Ordinal);
        foreach ((string key, object? tagValue) in tags)
        {
            tagValues[key] = tagValue?.ToString() ?? "";
        }
        _measurements.Add((instrument.Name, value, tagValues));
    }

    [Fact]
    public void CountsSetsAndEventsWithTheJavaTagValues()
    {
        MeterSsfReceiverMetrics metrics = new(_meter, issuer => (issuer == "https://idp.example") ? "idp" : issuer);
        metrics.SetReceived("https://idp.example", SsfDeliveryMethod.Push, SsfSetOutcome.InProgress);
        metrics.SetReceived(null, SsfDeliveryMethod.Poll, SsfSetOutcome.Handled);
        metrics.EventHandled("https://idp.example", SsfEventTypes.CaepSessionRevoked, SsfDeliveryMethod.Push);
        metrics.EventHandled("https://other.example", "https://events.example/custom", SsfDeliveryMethod.Poll);

        Assert.Equal(4, _measurements.Count);
        (string instrument, double value, Dictionary<string, string> tags) = _measurements[0];
        Assert.Equal("easyssf.receiver.sets", instrument);
        Assert.Equal(1, value);
        Assert.Equal(new Dictionary<string, string> { ["transmitter"] = "idp", ["delivery"] = "push", ["outcome"] = "in_progress" }, tags);
        Assert.Equal("unknown", _measurements[1].Tags["transmitter"]);
        Assert.Equal("poll", _measurements[1].Tags["delivery"]);
        Assert.Equal("easyssf.receiver.events", _measurements[2].Instrument);
        Assert.Equal("CaepSessionRevoked", _measurements[2].Tags["event"]);
        // a label function that returns the issuer tags with it, a vendor event is tagged with its URI
        Assert.Equal("https://other.example", _measurements[3].Tags["transmitter"]);
        Assert.Equal("https://events.example/custom", _measurements[3].Tags["event"]);
    }

    [Fact]
    public void RecordsPollDurationsAndPendingAcknowledgements()
    {
        MeterSsfReceiverMetrics metrics = new(_meter);
        metrics.PollCompleted("https://idp.example", TimeSpan.FromMilliseconds(250), success: false);
        Assert.Equal(("easyssf.receiver.poll", 0.25), (_measurements[0].Instrument, _measurements[0].Value));
        Assert.Equal("failure", _measurements[0].Tags["outcome"]);

        int pending = 3;
        metrics.PollerStarted("https://idp.example", () => pending);
        _listener.RecordObservableInstruments();
        (string Instrument, double Value, Dictionary<string, string> Tags) gauge = _measurements[^1];
        Assert.Equal("easyssf.receiver.poll.pending-acks", gauge.Instrument);
        Assert.Equal(3, gauge.Value);
        Assert.Equal("https://idp.example", gauge.Tags["transmitter"]);
        pending = 0;
        _listener.RecordObservableInstruments();
        Assert.Equal(0, _measurements[^1].Value);
    }

    [Fact]
    public void EveryOutcomeHasATag()
    {
        foreach (SsfSetOutcome outcome in Enum.GetValues<SsfSetOutcome>())
        {
            string tag = MeterSsfReceiverMetrics.Tag(outcome);
            Assert.Matches("^[a-z_]+$", tag);
        }
        Assert.Equal("in_progress", MeterSsfReceiverMetrics.Tag(SsfSetOutcome.InProgress));
    }
}
