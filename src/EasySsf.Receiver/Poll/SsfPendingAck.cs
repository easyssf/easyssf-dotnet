namespace EasySsf.Receiver.Poll;

/// <summary>
/// What the poller owes the transmitter for a SET it fetched (RFC 8936, section 2.2): an
/// acknowledgement that the SET was received and handled, or an error that it was rejected, which
/// goes into the <c>setErrs</c> member of the next poll request.
/// </summary>
public sealed record SsfPendingAck
{
    /// <param name="jti">the identifier of the SET</param>
    /// <param name="errorCode">the error code of a rejected SET (<c>invalid_request</c>, ...), <see langword="null"/> for an acknowledgement</param>
    /// <param name="errorDescription">a description of the error, may be <see langword="null"/></param>
    public SsfPendingAck(string jti, string? errorCode, string? errorDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        if (errorCode is null && errorDescription is not null)
        {
            throw new ArgumentException("an acknowledgement has no error description", nameof(errorDescription));
        }
        Jti = jti;
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
    }

    /// <summary>
    /// The identifier of the SET.
    /// </summary>
    public string Jti { get; }

    /// <summary>
    /// The error code of a rejected SET, <see langword="null"/> for an acknowledgement.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// A description of the error, may be <see langword="null"/>.
    /// </summary>
    public string? ErrorDescription { get; }

    /// <summary>
    /// The acknowledgement of a handled SET.
    /// </summary>
    public static SsfPendingAck Ack(string jti)
    {
        return new SsfPendingAck(jti, null, null);
    }

    /// <summary>
    /// The report of a rejected SET.
    /// </summary>
    public static SsfPendingAck Error(string jti, string errorCode, string? errorDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new SsfPendingAck(jti, errorCode, errorDescription);
    }

    /// <summary>
    /// Whether this reports an error rather than acknowledging the SET.
    /// </summary>
    public bool IsError => ErrorCode is not null;
}
