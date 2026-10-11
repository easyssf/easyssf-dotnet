using EasySsf.Core;

namespace EasySsf.Receiver.Metrics;

/// <summary>
/// <see cref="ISsfReceiverMetrics"/> that records nothing. The default.
/// </summary>
public sealed class NullSsfReceiverMetrics : ISsfReceiverMetrics
{
    private NullSsfReceiverMetrics()
    {
    }

    public static NullSsfReceiverMetrics Instance { get; } = new();

    public void SetReceived(string? transmitter, SsfDeliveryMethod deliveryMethod, SsfSetOutcome outcome)
    {
    }

    public void EventHandled(string transmitter, string eventType, SsfDeliveryMethod deliveryMethod)
    {
    }

    public void PollCompleted(string? transmitter, TimeSpan duration, bool success)
    {
    }

    public void PollerStarted(string? transmitter, Func<int> pendingAcks)
    {
    }
}
