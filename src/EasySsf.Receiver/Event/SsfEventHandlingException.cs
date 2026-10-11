namespace EasySsf.Receiver.Event;

/// <summary>
/// Thrown when at least one <see cref="ISsfEventHandler"/> failed to handle a verified SET.
/// </summary>
public class SsfEventHandlingException : Exception
{
    public SsfEventHandlingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
