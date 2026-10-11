namespace EasySsf.Receiver.Set;

/// <summary>
/// Thrown when a SET is rejected. Carries the error code reported to the transmitter (RFC 8935,
/// section 2.4).
/// </summary>
public class SsfSetVerificationException : Exception
{
    public const string InvalidRequest = "invalid_request";

    public const string InvalidKey = "invalid_key";

    public const string InvalidIssuer = "invalid_issuer";

    public const string InvalidAudience = "invalid_audience";

    /// <summary>
    /// The state of a verification event is not the expected one (SSF 1.0, section 8.1.4.1).
    /// </summary>
    public const string InvalidState = "invalid_state";

    public SsfSetVerificationException(string errorCode, string message)
        : this(errorCode, message, null)
    {
    }

    public SsfSetVerificationException(string errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>
    /// The error code reported to the transmitter: one of the constants of this class.
    /// </summary>
    public string ErrorCode { get; }
}
