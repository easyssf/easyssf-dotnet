namespace EasySsf.Receiver.Poll;

/// <summary>
/// Keeps the acknowledgements and error reports the poller owes a transmitter until a poll request
/// has carried them (RFC 8936, section 2.4: they ride on the next request). The
/// <see cref="InMemorySsfPollAckStore"/> loses them when the application stops, so a SET handled
/// but not yet acknowledged is delivered again after a restart and skipped as a duplicate. A
/// durable implementation acknowledges it with the first poll after the restart instead.
/// <para>
/// Entries are kept per transmitter, identified by its issuer, because a <c>jti</c> is only unique
/// per issuer and a store may serve several transmitters. Implementations have to be safe for
/// concurrent use.
/// </para>
/// </summary>
public interface ISsfPollAckStore
{
    /// <summary>
    /// Records an acknowledgement or error report. Recording the same <c>jti</c> again does nothing.
    /// </summary>
    /// <param name="issuer">the issuer of the transmitter the SET came from</param>
    /// <param name="ack">what to tell the transmitter</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask RecordAsync(string issuer, SsfPendingAck ack, CancellationToken cancellationToken = default);

    /// <summary>
    /// The acknowledgements and error reports waiting for the transmitter, oldest first.
    /// </summary>
    /// <param name="issuer">the issuer of the transmitter</param>
    /// <param name="limit">the maximum number to return</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask<IReadOnlyList<SsfPendingAck>> PendingAsync(string issuer, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes entries the transmitter has received.
    /// </summary>
    /// <param name="issuer">the issuer of the transmitter</param>
    /// <param name="jtis">the identifiers of the SETs whose entries were delivered</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask RemoveAsync(string issuer, IEnumerable<string> jtis, CancellationToken cancellationToken = default);

    /// <summary>
    /// The number of entries waiting for the transmitter.
    /// </summary>
    ValueTask<int> CountAsync(string issuer, CancellationToken cancellationToken = default);
}
