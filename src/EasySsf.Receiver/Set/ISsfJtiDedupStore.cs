using EasySsf.Core.Event;

namespace EasySsf.Receiver.Set;

/// <summary>
/// Remembers which SETs were processed, so that a redelivery of a handled SET is skipped and a SET
/// another instance is handling is left alone. It does not make handling exactly once: an instance
/// that dies after a handler ran and before <see cref="ProcessedAsync"/> hands the SET to the next
/// instance after the lease, which is why handlers are idempotent. Implement it to back it with a
/// shared or durable store.
/// <para>
/// A SET is <see cref="ClaimAsync">claimed</see> before its handlers run and marked
/// <see cref="ProcessedAsync">processed</see> once they succeeded. In between it is <em>in
/// progress</em>: another instance of the application that receives the same SET then neither
/// handles nor acknowledges it, and leaves it to the transmitter's redelivery. An instance that
/// crashes mid-processing leaves the claim behind, so a claim older than the store's lease counts
/// as abandoned and the next instance takes the SET over; handlers have to be idempotent for that
/// reason as well.
/// </para>
/// <para>
/// A granted claim carries a <see cref="SsfJtiClaim.Token"/> that fences it:
/// <see cref="ProcessedAsync"/> and <see cref="ForgetAsync"/> only act on the claim with that
/// token. An instance whose handlers outlived the lease therefore neither completes nor deletes
/// the claim of the instance that took the SET over; the SET belongs to the new holder.
/// </para>
/// </summary>
public interface ISsfJtiDedupStore
{
    /// <summary>
    /// Atomically records that the caller is about to handle the SET, unless it was handled before
    /// or is being handled by another instance.
    /// </summary>
    /// <returns>the state of the SET and, if it is new, the token of the claim</returns>
    ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the handlers of a claimed SET succeeded. Does nothing if the claim was taken
    /// over meanwhile, because the handlers outlived the lease: the SET then belongs to the
    /// instance that took it over.
    /// </summary>
    /// <param name="eventToken">the SET</param>
    /// <param name="claim">the claim <see cref="ClaimAsync"/> granted for the SET</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the given SET again, because handling it failed and it is expected to be delivered
    /// once more. Does nothing if the claim was taken over meanwhile.
    /// </summary>
    /// <param name="eventToken">the SET</param>
    /// <param name="claim">the claim <see cref="ClaimAsync"/> granted for the SET</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// The key identifying a SET, the same one handlers use for their side effects.
    /// </summary>
    /// <seealso cref="SsfEventToken.IdempotencyKey"/>
    static string Key(SsfEventToken eventToken)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        return eventToken.IdempotencyKey;
    }
}
