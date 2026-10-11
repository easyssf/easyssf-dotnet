namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// Thrown when the transmitter's metadata or keys cannot be obtained. Unlike an invalid SET this
/// is a temporary condition, the SET should be delivered again later.
/// </summary>
public class SsfTransmitterUnavailableException : Exception
{
    public SsfTransmitterUnavailableException(string message)
        : base(message)
    {
    }

    public SsfTransmitterUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
