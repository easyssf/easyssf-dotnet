namespace EasySsf.Receiver.Push;

/// <summary>
/// The response to a push request (RFC 8935, section 2.2 to 2.4).
/// </summary>
/// <param name="Status">the HTTP status</param>
/// <param name="Body">
/// the JSON error document, <see langword="null"/> if the response has no body; send it with the
/// <see cref="ContentType"/> and <see cref="ContentLanguage"/> headers
/// </param>
public sealed record SsfPushResponse(int Status, string? Body)
{
    /// <summary>
    /// The content type of <see cref="Body"/>.
    /// </summary>
    public const string ContentType = "application/json";

    /// <summary>
    /// The language of the description in <see cref="Body"/>, to be sent as
    /// <c>Content-Language</c> header (RFC 8935, section 2.3).
    /// </summary>
    public const string ContentLanguage = "en";

    /// <summary>
    /// Whether the SET was accepted.
    /// </summary>
    public bool IsAccepted => Status == 202;
}
