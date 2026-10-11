namespace EasySsf.Receiver.Metrics;

/// <summary>
/// What happened to a received SET, for <see cref="ISsfReceiverMetrics"/>.
/// </summary>
public enum SsfSetOutcome
{
    /// <summary>
    /// The SET was verified and handled.
    /// </summary>
    Handled,

    /// <summary>
    /// The SET was processed before and has been skipped.
    /// </summary>
    Duplicate,

    /// <summary>
    /// The SET is being handled by another instance and has been left for a redelivery.
    /// </summary>
    InProgress,

    /// <summary>
    /// The SET was rejected because it is invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// The SET was rejected because the transmitter did not authenticate.
    /// </summary>
    Unauthenticated,

    /// <summary>
    /// The SET could not be verified because the keys of the transmitter could not be obtained.
    /// </summary>
    Unavailable,

    /// <summary>
    /// An event handler failed.
    /// </summary>
    Failed,
}
