namespace EasySsf.Receiver.Event;

/// <summary>
/// Thrown when a SET is not handled because another instance of the application is handling it
/// right now (see <see cref="Set.SsfJtiClaimState.InProgress"/>). Like any
/// <see cref="SsfEventHandlingException"/> it leaves the SET unacknowledged, so that the
/// transmitter delivers it again, by which time the other instance has finished with it.
/// </summary>
public class SsfSetInProgressException : SsfEventHandlingException
{
    public SsfSetInProgressException(string message)
        : base(message, null)
    {
    }
}
