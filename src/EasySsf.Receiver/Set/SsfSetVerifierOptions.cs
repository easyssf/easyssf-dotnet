using EasySsf.Core.Event;

namespace EasySsf.Receiver.Set;

/// <summary>
/// How a <see cref="JwtSsfSetVerifier"/> verifies SETs. The verifier reads the options for every
/// SET, so a change takes effect for the next one; configure them before SETs arrive rather than
/// changing them while SETs are verified.
/// </summary>
public sealed class SsfSetVerifierOptions
{
    /// <summary>
    /// The JWS algorithms the verifier accepts by default: <c>RS256</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultAcceptedAlgorithms = new HashSet<string>(StringComparer.Ordinal) { "RS256" };

    /// <summary>
    /// The asymmetric signature algorithms SETs may be signed with: the RSA, RSA-PSS and ECDSA
    /// algorithms of RFC 7518, section 3.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedAlgorithms = new HashSet<string>(StringComparer.Ordinal)
    {
        "RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512",
    };

    /// <summary>
    /// The audiences a SET must be addressed to; it has to be addressed to at least one of them.
    /// Any audience is accepted while this is empty and <see cref="ExpectedAudiencesProvider"/>
    /// is <see langword="null"/>.
    /// </summary>
    public ICollection<string> ExpectedAudiences { get; } = new List<string>();

    /// <summary>
    /// Supplies the audiences a SET must be addressed to, consulted for every SET, instead of
    /// <see cref="ExpectedAudiences"/>: for example the audience of a stream once it is
    /// registered. Any audience is accepted while the provider returns none.
    /// </summary>
    public Func<IReadOnlyCollection<string>>? ExpectedAudiencesProvider { get; set; }

    /// <summary>
    /// The JWS algorithms SETs may be signed with, for example <c>RS256</c>. Only the asymmetric
    /// signature algorithms of <see cref="SupportedAlgorithms"/> are allowed.
    /// </summary>
    public ISet<string> AcceptedAlgorithms { get; } = new HashSet<string>(DefaultAcceptedAlgorithms, StringComparer.Ordinal);

    /// <summary>
    /// The minimum size of RSA signing keys in bits, <c>0</c> to accept any. 2048 by default.
    /// </summary>
    public int MinRsaKeySize { get; set; } = 2048;

    /// <summary>
    /// Whether a SET must have a <c>typ</c> header of <c>secevent+jwt</c> (RFC 8417, section
    /// 2.3). <see langword="true"/> by default.
    /// </summary>
    public bool RequireTypeHeader { get; set; } = true;

    /// <summary>
    /// How strictly the subject of a SET is validated: <see cref="SubjectCompatibilityMode.Strict"/>
    /// requires the top-level <c>sub_id</c> claim of SSF 1.0,
    /// <see cref="SubjectCompatibilityMode.Legacy"/> accepts SETs of transmitters following
    /// earlier drafts, which put the subject into the event payload.
    /// </summary>
    public SubjectCompatibilityMode SubjectCompatibility { get; set; } = SubjectCompatibilityMode.Strict;

    /// <summary>
    /// How far in the future a SET may be issued according to the local clock. One minute by
    /// default.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a fetched JWK Set is used before it is fetched again. Five minutes by default.
    /// </summary>
    public TimeSpan JwkSetCacheLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How soon after a fetch the JWK Set may be fetched again because a SET names a key that is
    /// not in it. A SET signed with an unknown key within that interval is rejected without a
    /// fetch, so that bogus SETs cannot make the receiver flood the transmitter. 30 seconds by
    /// default.
    /// </summary>
    public TimeSpan JwkSetRefreshRateLimit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The clock the verifier checks <c>iat</c> against and ages the JWK Set cache by.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
