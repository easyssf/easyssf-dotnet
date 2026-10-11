using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// Assembles an <see cref="SsfTransmitter"/>: set the parts as properties and call
/// <see cref="Build"/>. <see cref="MetadataResolver"/> and <see cref="Verifier"/> are required; the
/// rest depends on whether the receiver calls the transmitter and how SETs are delivered.
/// </summary>
public sealed class SsfTransmitterBuilder
{
    /// <param name="name">the name of the transmitter, <see cref="SsfTransmitter.DefaultName"/> for an unnamed one</param>
    /// <param name="issuer">the issuer of the transmitter</param>
    public SsfTransmitterBuilder(string name, string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        Name = name;
        Issuer = issuer;
    }

    public string Name { get; }

    public string Issuer { get; }

    /// <summary>
    /// Resolves the metadata of the transmitter. Required.
    /// </summary>
    public SsfTransmitterMetadataResolver? MetadataResolver { get; set; }

    /// <summary>
    /// Verifies the SETs of the transmitter. Required.
    /// </summary>
    public ISsfSetVerifier? Verifier { get; set; }

    /// <summary>
    /// Authenticates the receiver at the transmitter, <see langword="null"/> if the transmitter is
    /// not called.
    /// </summary>
    public ISsfTransmitterTokenProvider? TokenProvider { get; set; }

    /// <summary>
    /// Calls the stream management API, <see langword="null"/> if the transmitter is not called.
    /// </summary>
    public SsfStreamClient? StreamClient { get; set; }

    /// <summary>
    /// Holds the stream once it is known. A fresh one by default.
    /// </summary>
    public SsfReceiverStream ReceiverStream { get; set; } = new();

    /// <summary>
    /// Validates verification events; one that checks the identifier of the
    /// <see cref="ReceiverStream"/> if <see langword="null"/>.
    /// </summary>
    public SsfStreamVerification? StreamVerification { get; set; }

    /// <summary>
    /// Looks up or registers the stream, <see langword="null"/> if the stream is neither looked up
    /// nor managed.
    /// </summary>
    public SsfStreamRegistrar? StreamRegistrar { get; set; }

    /// <summary>
    /// Polls the transmitter, <see langword="null"/> with PUSH delivery.
    /// </summary>
    public SsfPoller? Poller { get; set; }

    /// <summary>
    /// Whether <see cref="SsfTransmitter.Start"/> starts the <see cref="Poller"/>. <see langword="true"/>
    /// by default.
    /// </summary>
    public bool AutoStartPolling { get; set; } = true;

    /// <summary>
    /// The <c>Authorization</c> header the transmitter has to send to the push endpoint,
    /// <see langword="null"/> or blank if none is expected.
    /// </summary>
    public string? PushAuthorizationHeader { get; set; }

    public SsfTransmitter Build()
    {
        ArgumentNullException.ThrowIfNull(MetadataResolver, nameof(MetadataResolver));
        ArgumentNullException.ThrowIfNull(Verifier, nameof(Verifier));
        ArgumentNullException.ThrowIfNull(ReceiverStream, nameof(ReceiverStream));
        if (StreamVerification is null)
        {
            SsfReceiverStream receiverStream = ReceiverStream;
            StreamVerification = new SsfStreamVerification { StreamId = () => receiverStream.StreamId };
        }
        return new SsfTransmitter(this);
    }
}
