using System.Collections.ObjectModel;

using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// The transmitters of a receiver, by name and by issuer. Most applications have one, the
/// <see cref="SsfTransmitter.DefaultName">default</see> one.
/// </summary>
public sealed class SsfTransmitters
{
    private readonly ReadOnlyDictionary<string, SsfTransmitter> _byName;

    private readonly ReadOnlyDictionary<string, SsfTransmitter> _byIssuer;

    public SsfTransmitters(IEnumerable<SsfTransmitter> transmitters)
    {
        ArgumentNullException.ThrowIfNull(transmitters);
        OrderedDictionary<string, SsfTransmitter> byName = new(StringComparer.Ordinal);
        OrderedDictionary<string, SsfTransmitter> byIssuer = new(StringComparer.Ordinal);
        OrderedDictionary<string, ISsfSetVerifier> verifiers = new(StringComparer.Ordinal);
        foreach (SsfTransmitter transmitter in transmitters)
        {
            if (!byName.TryAdd(transmitter.Name, transmitter))
            {
                throw new ArgumentException($"Two transmitters are named '{transmitter.Name}'", nameof(transmitters));
            }
            if (byIssuer.TryGetValue(transmitter.Issuer, out SsfTransmitter? sameIssuer))
            {
                throw new ArgumentException($"The transmitters '{transmitter.Name}' and '{sameIssuer.Name}' have the same issuer {transmitter.Issuer}",
                    nameof(transmitters));
            }
            byIssuer[transmitter.Issuer] = transmitter;
            verifiers[transmitter.Issuer] = transmitter.Verifier;
        }
        if (byName.Count == 0)
        {
            throw new ArgumentException("transmitters must not be empty", nameof(transmitters));
        }
        _byName = new ReadOnlyDictionary<string, SsfTransmitter>(byName);
        _byIssuer = new ReadOnlyDictionary<string, SsfTransmitter>(byIssuer);
        Verifier = new IssuerRoutingSsfSetVerifier(verifiers);
    }

    /// <summary>
    /// All transmitters, in the order given.
    /// </summary>
    public IReadOnlyList<SsfTransmitter> All => _byName.Values.ToList().AsReadOnly();

    /// <summary>
    /// The transmitter with the given name, <see langword="null"/> if there is none.
    /// </summary>
    public SsfTransmitter? Get(string name)
    {
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>
    /// The transmitter with the given issuer, <see langword="null"/> if there is none.
    /// </summary>
    public SsfTransmitter? ByIssuer(string? issuer)
    {
        return (issuer is not null) ? _byIssuer.GetValueOrDefault(issuer) : null;
    }

    /// <summary>
    /// The transmitter if there is exactly one, else the default one if there is one, else
    /// <see langword="null"/>.
    /// </summary>
    public SsfTransmitter? Primary => (_byName.Count == 1) ? _byName.Values.First() : Get(SsfTransmitter.DefaultName);

    /// <summary>
    /// The short name of a transmitter, for labels such as metric tags, where its issuer would be
    /// long.
    /// </summary>
    /// <returns>the name of the transmitter with the given issuer, or the issuer itself if no transmitter has it</returns>
    public string? NameOf(string? issuer)
    {
        return ByIssuer(issuer)?.Name ?? issuer;
    }

    /// <summary>
    /// A verifier that verifies the SETs of every transmitter, by issuer.
    /// </summary>
    public IssuerRoutingSsfSetVerifier Verifier { get; }

    /// <summary>
    /// The stream verification of the transmitter with the given issuer, <see langword="null"/> for
    /// an unknown issuer.
    /// </summary>
    public ISsfStreamVerification? StreamVerification(string issuer)
    {
        return ByIssuer(issuer)?.StreamVerification;
    }

    /// <summary>
    /// The <c>critical_subject_members</c> the transmitter with the given issuer declares in its
    /// metadata, empty if none or the issuer is unknown. The metadata is the resolved one: a SET of
    /// the transmitter was verified with its keys before.
    /// </summary>
    public IReadOnlyCollection<string> CriticalSubjectMembers(string issuer)
    {
        return ByIssuer(issuer)?.MetadataResolver.ResolvedMetadata?.CriticalSubjectMembers ?? [];
    }

    /// <summary>
    /// The <c>Authorization</c> header the transmitter with the given issuer has to send to the push
    /// endpoint, <see langword="null"/> if none is expected or the issuer is unknown.
    /// </summary>
    public string? PushAuthorizationHeader(string? issuer)
    {
        return ByIssuer(issuer)?.PushAuthorizationHeader;
    }

    /// <summary>
    /// Starts the background work of every transmitter.
    /// </summary>
    public void Start()
    {
        foreach (SsfTransmitter transmitter in _byName.Values)
        {
            transmitter.Start();
        }
    }

    /// <summary>
    /// Stops the background work of every transmitter.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (SsfTransmitter transmitter in _byName.Values)
        {
            await transmitter.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
