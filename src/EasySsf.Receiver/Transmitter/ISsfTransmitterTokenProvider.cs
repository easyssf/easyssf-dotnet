namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// Provides the access token the receiver authenticates with when it calls the stream management
/// and poll endpoints of the transmitter. Implement it to obtain the token in another way than the
/// built-in ones.
/// </summary>
public interface ISsfTransmitterTokenProvider
{
    /// <returns>
    /// the access token to send as bearer token, or <see langword="null"/> to call the transmitter
    /// without authentication
    /// </returns>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the transmitter rejected the access token (HTTP 401), so that the next call of
    /// <see cref="GetAccessTokenAsync"/> returns a new one. Does nothing by default.
    /// </summary>
    void Invalidate()
    {
    }
}

/// <summary>
/// Token providers for the simple cases.
/// </summary>
public static class SsfTransmitterTokenProviders
{
    /// <summary>
    /// Calls the transmitter without authentication.
    /// </summary>
    public static ISsfTransmitterTokenProvider None { get; } = new FixedTokenProvider(null);

    /// <summary>
    /// Always sends the given token, for example one issued out of band.
    /// </summary>
    /// <param name="accessToken">the access token, <see langword="null"/> for no authentication</param>
    public static ISsfTransmitterTokenProvider Fixed(string? accessToken)
    {
        return new FixedTokenProvider(accessToken);
    }

    private sealed class FixedTokenProvider(string? accessToken) : ISsfTransmitterTokenProvider
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(accessToken);
        }
    }
}
