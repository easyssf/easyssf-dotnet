namespace EasySsf.Receiver.Set;

/// <summary>
/// What an <see cref="ISsfJtiDedupStore"/> knows about a SET when it is claimed.
/// </summary>
public enum SsfJtiClaimState
{
    /// <summary>
    /// Not seen before, or abandoned by a crashed instance: the caller handles the SET.
    /// </summary>
    New,

    /// <summary>
    /// Another instance is handling the SET right now: neither handle nor acknowledge it.
    /// </summary>
    InProgress,

    /// <summary>
    /// The SET was handled before: acknowledge it, do not handle it again.
    /// </summary>
    Processed,
}

/// <summary>
/// The outcome of <see cref="ISsfJtiDedupStore.ClaimAsync"/>: the <see cref="SsfJtiClaimState"/>
/// of the SET and, for a <see cref="SsfJtiClaimState.New">new</see> claim, the token that
/// identifies this claim against a later take-over of the same SET.
/// </summary>
/// <param name="State">what the store knew about the SET</param>
/// <param name="Token">
/// the token of the claim granted, meaningless unless the state is
/// <see cref="SsfJtiClaimState.New"/>; a value the store chooses, for example the time of the claim
/// </param>
public sealed record SsfJtiClaim(SsfJtiClaimState State, long Token)
{
    /// <summary>
    /// The SET is being handled by another instance.
    /// </summary>
    public static SsfJtiClaim InProgress { get; } = new(SsfJtiClaimState.InProgress, 0);

    /// <summary>
    /// The SET was handled before.
    /// </summary>
    public static SsfJtiClaim Processed { get; } = new(SsfJtiClaimState.Processed, 0);

    /// <summary>
    /// A claim granted to the caller, who handles the SET.
    /// </summary>
    public static SsfJtiClaim Granted(long token)
    {
        return new SsfJtiClaim(SsfJtiClaimState.New, token);
    }

    /// <summary>
    /// Whether the claim was granted: the caller handles the SET.
    /// </summary>
    public bool IsNew => State == SsfJtiClaimState.New;
}
