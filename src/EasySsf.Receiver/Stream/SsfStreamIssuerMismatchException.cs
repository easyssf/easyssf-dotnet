using EasySsf.Core.Stream;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Thrown when the transmitter created a stream whose issuer is not the issuer the transmitter
/// metadata was obtained from (SSF 1.0, section 8.1.1.1). Such a stream must not be used.
/// </summary>
public class SsfStreamIssuerMismatchException : SsfStreamException
{
    public SsfStreamIssuerMismatchException(string expectedIssuer, SsfStreamConfiguration stream)
        : base($"The transmitter created stream {stream?.StreamId} with the issuer '{stream?.Issuer}' instead of '{expectedIssuer}'", 0, null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream = stream;
    }

    /// <summary>
    /// The stream the transmitter created, for example to delete it.
    /// </summary>
    public SsfStreamConfiguration Stream { get; }
}
