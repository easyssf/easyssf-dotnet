namespace EasySsf.Receiver.Set;

/// <summary>
/// What happened to a valid SET in <see cref="SsfSetProcessor.ProcessAsync(string, Core.SsfDeliveryMethod, CancellationToken)"/>.
/// </summary>
public enum SsfSetProcessingOutcome
{
    /// <summary>
    /// The SET was handled by all handlers.
    /// </summary>
    Handled,

    /// <summary>
    /// The SET was processed before and has been skipped.
    /// </summary>
    Duplicate,
}
