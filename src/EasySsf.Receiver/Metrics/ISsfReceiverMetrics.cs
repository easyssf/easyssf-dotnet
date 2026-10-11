using EasySsf.Core;

namespace EasySsf.Receiver.Metrics;

/// <summary>
/// Records what the receiver does. <see cref="NullSsfReceiverMetrics"/> records nothing,
/// <see cref="MeterSsfReceiverMetrics"/> records to <c>System.Diagnostics.Metrics</c>.
/// </summary>
public interface ISsfReceiverMetrics
{
    /// <summary>
    /// A SET was received from a transmitter.
    /// </summary>
    /// <param name="transmitter">the issuer of the transmitter, <see langword="null"/> if the SET did not tell</param>
    /// <param name="deliveryMethod">how the SET was delivered</param>
    /// <param name="outcome">what happened to it</param>
    void SetReceived(string? transmitter, SsfDeliveryMethod deliveryMethod, SsfSetOutcome outcome);

    /// <summary>
    /// An event of a SET of a transmitter was handled.
    /// </summary>
    /// <param name="transmitter">the issuer of the transmitter</param>
    /// <param name="eventType">the event type URI</param>
    /// <param name="deliveryMethod">how the SET was delivered</param>
    void EventHandled(string transmitter, string eventType, SsfDeliveryMethod deliveryMethod);

    /// <summary>
    /// A transmitter was polled for SETs.
    /// </summary>
    /// <param name="transmitter">the issuer of the transmitter, <see langword="null"/> if unknown</param>
    /// <param name="duration">how long the poll request took</param>
    /// <param name="success">whether the transmitter answered the poll request</param>
    void PollCompleted(string? transmitter, TimeSpan duration, bool success);

    /// <summary>
    /// A poller started and tells how to read the number of acknowledgements and error reports it
    /// still owes its transmitter, for a gauge.
    /// </summary>
    /// <param name="transmitter">the issuer of the transmitter, <see langword="null"/> if unknown</param>
    /// <param name="pendingAcks">reads the current number</param>
    void PollerStarted(string? transmitter, Func<int> pendingAcks);
}
