using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// Everything the receiver holds for one transmitter: how its SETs are verified, how it is called,
/// the stream of the receiver at it, and the background work for it. Build it with an
/// <see cref="SsfTransmitterBuilder"/>.
/// </summary>
public sealed class SsfTransmitter
{
    /// <summary>
    /// The name of the transmitter an application configures without naming it.
    /// </summary>
    public const string DefaultName = "default";

    internal SsfTransmitter(SsfTransmitterBuilder builder)
    {
        Name = builder.Name;
        Issuer = builder.Issuer;
        MetadataResolver = builder.MetadataResolver!;
        Verifier = builder.Verifier!;
        TokenProvider = builder.TokenProvider;
        StreamClient = builder.StreamClient;
        ReceiverStream = builder.ReceiverStream;
        StreamVerification = builder.StreamVerification!;
        StreamRegistrar = builder.StreamRegistrar;
        Poller = builder.Poller;
        AutoStartPolling = builder.AutoStartPolling;
        PushAuthorizationHeader = string.IsNullOrWhiteSpace(builder.PushAuthorizationHeader) ? null : builder.PushAuthorizationHeader;
        if (StreamRegistrar is not null && Poller is not null)
        {
            // the first poll right after the stream is registered, not an interval later
            SsfPoller poller = Poller;
            StreamRegistrar.AddListener(_ => poller.WakeUp());
        }
    }

    public string Name { get; }

    public string Issuer { get; }

    public SsfTransmitterMetadataResolver MetadataResolver { get; }

    public ISsfSetVerifier Verifier { get; }

    /// <summary>
    /// The token provider, <see langword="null"/> if the transmitter is not called.
    /// </summary>
    public ISsfTransmitterTokenProvider? TokenProvider { get; }

    /// <summary>
    /// The stream client, <see langword="null"/> if the transmitter is not called.
    /// </summary>
    public SsfStreamClient? StreamClient { get; }

    public SsfReceiverStream ReceiverStream { get; }

    public SsfStreamVerification StreamVerification { get; }

    /// <summary>
    /// The registrar, <see langword="null"/> if the stream is neither looked up nor managed.
    /// </summary>
    public SsfStreamRegistrar? StreamRegistrar { get; }

    /// <summary>
    /// The poller, <see langword="null"/> with PUSH delivery.
    /// </summary>
    public SsfPoller? Poller { get; }

    /// <summary>
    /// Whether <see cref="Start"/> starts the <see cref="Poller"/>.
    /// </summary>
    public bool AutoStartPolling { get; }

    /// <summary>
    /// The <c>Authorization</c> header the transmitter has to send to the push endpoint,
    /// <see langword="null"/> if none is expected.
    /// </summary>
    public string? PushAuthorizationHeader { get; }

    /// <summary>
    /// Starts the background work: looking up or registering the stream, and polling.
    /// </summary>
    public void Start()
    {
        StreamRegistrar?.Start();
        if (Poller is not null && AutoStartPolling)
        {
            Poller.Start();
        }
    }

    /// <summary>
    /// Stops the background work: the poller first, then the registrar.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Poller is not null)
        {
            await Poller.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        if (StreamRegistrar is not null)
        {
            await StreamRegistrar.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override string ToString()
    {
        return Name + " (" + Issuer + ")";
    }
}
