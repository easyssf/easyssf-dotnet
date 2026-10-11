using EasySsf.Core.Stream;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Holds the configuration of the stream this receiver gets its events from, once it is known:
/// after the <see cref="SsfStreamRegistrar"/> has registered or looked up the stream at the
/// transmitter.
/// </summary>
public sealed class SsfReceiverStream
{
    private volatile SsfStreamConfiguration? _configuration;

    /// <summary>
    /// The configuration of the stream, <see langword="null"/> as long as it is not known.
    /// </summary>
    public SsfStreamConfiguration? Configuration
    {
        get => _configuration;
        set => _configuration = value;
    }

    /// <summary>
    /// The identifier of the stream, <see langword="null"/> as long as it is not known.
    /// </summary>
    public string? StreamId => _configuration?.StreamId;

    /// <summary>
    /// The audiences of the SETs of the stream, empty as long as they are not known.
    /// </summary>
    public IReadOnlyList<string> Audience => _configuration?.Audience ?? [];
}
