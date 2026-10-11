namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// How a <see cref="ClientCredentialsSsfTransmitterTokenProvider"/> obtains tokens.
/// </summary>
public sealed class SsfClientCredentialsOptions
{
    /// <summary>
    /// The scopes to request, none by default.
    /// </summary>
    public IList<string> Scopes { get; } = [];

    /// <summary>
    /// Whether the client credentials are sent in the request body (<c>client_secret_post</c>)
    /// instead of with HTTP basic authentication (<c>client_secret_basic</c>).
    /// </summary>
    public bool AuthenticateWithRequestBody { get; set; }

    /// <summary>
    /// How long before its expiry a token is renewed, 30 seconds by default; never more than a
    /// quarter of the token's lifetime.
    /// </summary>
    public TimeSpan ExpirySafetyWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Form parameters to send with the token request in addition to the grant, for extensions of
    /// the token endpoint.
    /// </summary>
    public IDictionary<string, string> AdditionalParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The clock the expiry is measured by.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
