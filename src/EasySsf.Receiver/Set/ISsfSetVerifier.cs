using EasySsf.Core.Event;
using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Set;

/// <summary>
/// Verifies an encoded Security Event Token (RFC 8417).
/// </summary>
public interface ISsfSetVerifier
{
    /// <summary>
    /// Verifies the signature and claims of the given SET.
    /// </summary>
    /// <param name="encodedSet">the SET in JWS compact serialization</param>
    /// <param name="cancellationToken">cancels the verification</param>
    /// <returns>the verified token</returns>
    /// <exception cref="SsfSetVerificationException">if the SET is invalid</exception>
    /// <exception cref="SsfTransmitterUnavailableException">
    /// if the transmitter's metadata or keys cannot be obtained
    /// </exception>
    Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default);
}
