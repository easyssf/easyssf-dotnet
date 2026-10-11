using EasySsf.Core.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Stream;

namespace EasySsf.Receiver.Set;

/// <summary>
/// How an <see cref="SsfSetProcessor"/> processes SETs beyond verifying, de-duplicating and
/// handling them. Read for every SET.
/// </summary>
public sealed class SsfSetProcessorOptions
{
    /// <summary>
    /// Records what the processor does. Records nothing by default.
    /// </summary>
    public ISsfReceiverMetrics Metrics { get; set; } = NullSsfReceiverMetrics.Instance;

    /// <summary>
    /// The stream verification of a transmitter by its issuer, <see langword="null"/> for none:
    /// validates stream verification events before the handlers see them. For a receiver with one
    /// transmitter, return the same instance for every issuer. Verification events are accepted
    /// unchecked while this is <see langword="null"/>.
    /// </summary>
    public Func<string, ISsfStreamVerification?>? StreamVerifications { get; set; }

    /// <summary>
    /// The members of a complex subject the application interprets. A SET whose subject has a
    /// member the transmitter declared critical (<c>critical_subject_members</c> of its metadata)
    /// that is not among them is rejected as <c>invalid_request</c>, as the SSF specification
    /// requires critical members to be interpreted by the receiver. By default the members
    /// <see cref="SsfSubject"/> gives access to, <see cref="SsfSubject.Members"/>.
    /// </summary>
    public ISet<string> UnderstoodSubjectMembers { get; } = new HashSet<string>(SsfSubject.Members, StringComparer.Ordinal);

    /// <summary>
    /// The <c>critical_subject_members</c> a transmitter declares in its metadata, by its issuer;
    /// empty or <see langword="null"/> for an unknown issuer. Nothing is critical while this is
    /// <see langword="null"/>.
    /// </summary>
    /// <seealso cref="UnderstoodSubjectMembers"/>
    public Func<string, IReadOnlyCollection<string>?>? CriticalSubjectMembers { get; set; }

    /// <summary>
    /// Renews the claim of a SET at this interval while its handlers run
    /// (<see cref="ISsfJtiDedupStore.RenewAsync"/>), so that a handler that runs longer than the
    /// lease of the de-duplication store does not lose the SET to another instance; a third of the
    /// lease is a good interval. <see langword="null"/>, the default, turns renewal off: the lease
    /// then bounds the handlers. A renewal that finds the claim taken over stops, and the result of
    /// the handlers is not recorded, as without renewal after the lease.
    /// </summary>
    public TimeSpan? ClaimRenewal { get; set; }
}
