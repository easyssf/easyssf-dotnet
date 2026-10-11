namespace EasySsf.Receiver.Poll;

/// <summary>
/// Thrown when a poll request to the transmitter failed.
/// </summary>
public class SsfPollException : Exception
{
    public SsfPollException(string message, int statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// The HTTP status the transmitter answered with, <c>0</c> if it did not answer.
    /// </summary>
    public int StatusCode { get; }
}
