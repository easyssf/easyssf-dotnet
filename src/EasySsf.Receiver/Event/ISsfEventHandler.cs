namespace EasySsf.Receiver.Event;

/// <summary>
/// Reacts to verified security events. Every handler given to the
/// <see cref="Set.SsfSetProcessor"/> is invoked for every SET.
/// <para>
/// A SET is handled at least once, not exactly once, so handlers must be idempotent. A SET is
/// delivered again if any handler failed, and all handlers run again, possibly on another
/// instance of the application; with a shared dedup store a SET is claimed while its handlers run
/// and another instance leaves it alone meanwhile, but an instance that dies after a handler's
/// side effects and before the success is recorded hands the SET to the next instance after a
/// lease. Side effects outside the receiver's stores should be keyed by
/// <see cref="SsfEventContext.IdempotencyKey"/>.
/// </para>
/// </summary>
public interface ISsfEventHandler
{
    /// <summary>
    /// Handles the given SET. Throwing makes the receiver report a failure to the transmitter,
    /// which is then expected to deliver the SET again.
    /// </summary>
    Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default);
}
