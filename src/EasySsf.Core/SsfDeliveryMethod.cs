namespace EasySsf.Core;

/// <summary>
/// How SETs get from the transmitter to the receiver.
/// </summary>
public enum SsfDeliveryMethod
{
    /// <summary>
    /// The transmitter sends SETs to an endpoint of the receiver (RFC 8935).
    /// </summary>
    Push,

    /// <summary>
    /// The receiver fetches SETs from an endpoint of the transmitter (RFC 8936).
    /// </summary>
    Poll,
}

/// <summary>
/// The identifiers of the delivery methods in a stream configuration (SSF 1.0, section 7.1.1).
/// </summary>
public static class SsfDeliveryMethods
{
    /// <summary>
    /// The identifier of push delivery, <c>urn:ietf:rfc:8935</c>.
    /// </summary>
    public const string PushUri = "urn:ietf:rfc:8935";

    /// <summary>
    /// The identifier of poll delivery, <c>urn:ietf:rfc:8936</c>.
    /// </summary>
    public const string PollUri = "urn:ietf:rfc:8936";

    /// <summary>
    /// The identifier of the delivery method in a stream configuration.
    /// </summary>
    public static string Uri(this SsfDeliveryMethod method)
    {
        return method switch
        {
            SsfDeliveryMethod.Push => PushUri,
            SsfDeliveryMethod.Poll => PollUri,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown delivery method"),
        };
    }

    /// <summary>
    /// Resolves the identifier of a delivery method.
    /// </summary>
    /// <returns>the method, <see langword="null"/> if the identifier is not one of the known methods</returns>
    public static SsfDeliveryMethod? FromUri(string? uri)
    {
        return uri switch
        {
            PushUri => SsfDeliveryMethod.Push,
            PollUri => SsfDeliveryMethod.Poll,
            _ => null,
        };
    }
}
