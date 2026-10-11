namespace EasySsf.Receiver.Stream;

/// <summary>
/// Thrown when a call to the stream management API of the transmitter failed.
/// </summary>
public class SsfStreamException : Exception
{
    public SsfStreamException(string message, int statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// The HTTP status the transmitter answered with, <c>0</c> if it did not answer.
    /// </summary>
    public int StatusCode { get; }
}
