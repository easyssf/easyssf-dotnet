namespace EasySsf.Receiver.Stream;

/// <summary>
/// Where the registration of an <see cref="SsfStreamRegistrar"/> stands.
/// </summary>
public enum SsfStreamRegistrarState
{
    /// <summary>
    /// <see cref="SsfStreamRegistrar.Start"/> was not called yet.
    /// </summary>
    NotStarted,

    /// <summary>
    /// The stream is being looked up or registered, with retries while the transmitter cannot be
    /// reached.
    /// </summary>
    Registering,

    /// <summary>
    /// The stream is known and matches what the receiver wants.
    /// </summary>
    Registered,

    /// <summary>
    /// The stream cannot be used, see <see cref="SsfStreamRegistrar.LastError"/>. Not retried.
    /// </summary>
    Failed,
}
