using System.Collections.ObjectModel;

using EasySsf.Core.Event;

namespace EasySsf.Receiver.Set;

/// <summary>
/// Verifies SETs of several transmitters: the <c>iss</c> claim of a SET selects the
/// <see cref="ISsfSetVerifier"/> of that transmitter, which then verifies the SET, including its
/// issuer. A SET of an unknown issuer is rejected.
/// </summary>
public sealed class IssuerRoutingSsfSetVerifier : ISsfSetVerifier
{
    /// <param name="verifiers">the verifier of each transmitter by its issuer</param>
    public IssuerRoutingSsfSetVerifier(IEnumerable<KeyValuePair<string, ISsfSetVerifier>> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        Verifiers = new ReadOnlyDictionary<string, ISsfSetVerifier>(
            new OrderedDictionary<string, ISsfSetVerifier>(verifiers, StringComparer.Ordinal));
        if (Verifiers.Count == 0)
        {
            throw new ArgumentException("verifiers must not be empty", nameof(verifiers));
        }
    }

    /// <summary>
    /// The verifier of each transmitter by its issuer.
    /// </summary>
    public IReadOnlyDictionary<string, ISsfSetVerifier> Verifiers { get; }

    public Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
    {
        string? issuer = SsfSetClaims.UnverifiedIssuer(encodedSet);
        if (issuer is null)
        {
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidRequest,
                "The SET cannot be parsed or has no issuer");
        }
        if (!Verifiers.TryGetValue(issuer, out ISsfSetVerifier? verifier))
        {
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidIssuer,
                $"The SET was issued by '{issuer}', which is not a transmitter of this receiver");
        }
        return verifier.VerifyAsync(encodedSet, cancellationToken);
    }
}
