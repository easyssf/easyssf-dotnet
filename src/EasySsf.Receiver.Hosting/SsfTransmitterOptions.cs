using EasySsf.Core;
using EasySsf.Core.Event;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// The settings of one SSF transmitter. <see cref="SsfReceiverOptions"/> binds them at
/// <c>EasySsf:Receiver</c> for the default transmitter, and at
/// <c>EasySsf:Receiver:Transmitters:&lt;name&gt;</c> for every other one.
/// </summary>
public class SsfTransmitterOptions
{
    /// <summary>
    /// Issuer of the SSF transmitter. Every inbound SET must carry this value as its <c>iss</c>
    /// claim.
    /// </summary>
    public string? TransmitterIssuer { get; set; }

    /// <summary>
    /// Location of the transmitter's SSF configuration metadata. Derived from the transmitter
    /// issuer when not set.
    /// </summary>
    public Uri? TransmitterMetadataUrl { get; set; }

    /// <summary>
    /// Location of the JWK Set used to verify SET signatures. Discovered from the transmitter
    /// metadata (<c>jwks_uri</c>) when not set.
    /// </summary>
    public Uri? TransmitterJwksUrl { get; set; }

    /// <summary>
    /// Whether the transmitter issuer and the endpoints it publishes may use plain <c>http</c> on
    /// hosts other than loopback addresses. For test setups only, SSF requires <c>https</c>.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Audience this receiver is known as at the transmitter. When set, every inbound SET must
    /// contain it in its <c>aud</c> claim.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// How the transmitter delivers SETs: <c>Push</c> to the endpoint of this application or
    /// <c>Poll</c> from an endpoint of the transmitter.
    /// </summary>
    public SsfDeliveryMethod DeliveryMethod { get; set; } = SsfDeliveryMethod.Push;

    /// <summary>
    /// Access token to authenticate with at the stream management and poll endpoints of the
    /// transmitter, for transmitters that hand out long-lived tokens.
    /// </summary>
    public string? TransmitterAccessToken { get; set; }

    /// <summary>
    /// OAuth2 client credentials to authenticate with at the stream management and poll endpoints
    /// of the transmitter.
    /// </summary>
    public SsfOAuth2Options OAuth2 { get; } = new();

    /// <summary>
    /// The event stream of this receiver at the transmitter.
    /// </summary>
    public SsfStreamOptions Stream { get; } = new();

    /// <summary>
    /// POLL delivery (RFC 8936).
    /// </summary>
    public SsfPollOptions Poll { get; } = new();

    /// <summary>
    /// How SETs are validated.
    /// </summary>
    public SsfSetValidationOptions SetValidation { get; } = new();

    /// <summary>
    /// PUSH delivery (RFC 8935) from this transmitter.
    /// </summary>
    public SsfPushOptions Push { get; } = new();
}

/// <summary>
/// OAuth2 client credentials to authenticate with at the transmitter.
/// </summary>
public sealed class SsfOAuth2Options
{
    /// <summary>
    /// Token endpoint to obtain access tokens from with the client credentials grant.
    /// </summary>
    public Uri? TokenUri { get; set; }

    /// <summary>
    /// Client id of this receiver.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret of this receiver.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scopes to request.
    /// </summary>
    public List<string> Scopes { get; } = [];

    /// <summary>
    /// How the client authenticates at the token endpoint: <c>Basic</c>
    /// (<c>client_secret_basic</c>) or <c>Post</c> (<c>client_secret_post</c>).
    /// </summary>
    public SsfClientAuthenticationMethod ClientAuthenticationMethod { get; set; } = SsfClientAuthenticationMethod.Basic;

    /// <summary>
    /// How long before its expiry an access token is renewed; never more than a quarter of the
    /// token's lifetime.
    /// </summary>
    public TimeSpan ExpirySafetyWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Form parameters to send with the token request in addition to the grant, for extensions of
    /// the token endpoint.
    /// </summary>
    public Dictionary<string, string> AdditionalParameters { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// How the receiver authenticates at the token endpoint (RFC 6749, section 2.3.1).
/// </summary>
public enum SsfClientAuthenticationMethod
{
    /// <summary>
    /// <c>client_secret_basic</c>: HTTP basic authentication.
    /// </summary>
    Basic,

    /// <summary>
    /// <c>client_secret_post</c>: the credentials in the request body.
    /// </summary>
    Post,
}

/// <summary>
/// The event stream of this receiver at the transmitter.
/// </summary>
public sealed class SsfStreamOptions
{
    /// <summary>
    /// Who manages the stream: with <c>Transmitter</c> the stream is created at the transmitter,
    /// with <c>Receiver</c> this application looks its stream up on startup and creates or updates
    /// it if necessary.
    /// </summary>
    public SsfStreamManagement Management { get; set; } = SsfStreamManagement.Transmitter;

    /// <summary>
    /// Identifier of a stream that was created at the transmitter. When set, the stream is looked
    /// up on startup to learn its audience and poll endpoint.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The event types requested when <see cref="EventsRequested"/> is left empty:
    /// <c>CaepSessionRevoked</c> and <c>CaepCredentialChange</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultEventsRequested = ["CaepSessionRevoked", "CaepCredentialChange"];

    /// <summary>
    /// Event types (alias or URI) requested for a stream managed by the receiver. Empty, the
    /// default, requests <see cref="DefaultEventsRequested"/>: configuration binding adds to a list,
    /// so the defaults are not in the list itself.
    /// </summary>
    public List<string> EventsRequested { get; } = [];

    /// <summary>
    /// The event types a stream managed by the receiver requests: <see cref="EventsRequested"/>, or
    /// <see cref="DefaultEventsRequested"/> if none are configured.
    /// </summary>
    public IReadOnlyList<string> EffectiveEventsRequested => (EventsRequested.Count > 0) ? EventsRequested : DefaultEventsRequested;

    /// <summary>
    /// Description of a stream managed by the receiver.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether a stream managed by the receiver is deleted at the transmitter when the application
    /// stops.
    /// </summary>
    public bool DeleteOnShutdown { get; set; }
}

/// <summary>
/// Who manages the stream of the receiver.
/// </summary>
public enum SsfStreamManagement
{
    /// <summary>
    /// The stream is created at the transmitter, by an administrator or the transmitter itself.
    /// </summary>
    Transmitter,

    /// <summary>
    /// The receiver looks its stream up on startup and creates or updates it if necessary.
    /// </summary>
    Receiver,
}

/// <summary>
/// POLL delivery (RFC 8936).
/// </summary>
public sealed class SsfPollOptions
{
    /// <summary>
    /// Poll endpoint of the transmitter. Taken from the stream configuration when not set.
    /// </summary>
    public Uri? EndpointUrl { get; set; }

    /// <summary>
    /// Whether the transmitter is polled periodically. Otherwise the application polls by calling
    /// <c>SsfPoller.PollNowAsync()</c>.
    /// </summary>
    public bool AutoStartup { get; set; } = true;

    /// <summary>
    /// Time between two polls.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time to wait before the first poll.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum number of SETs to fetch with one request.
    /// </summary>
    public int MaxEvents { get; set; } = 100;

    /// <summary>
    /// Whether to long poll (RFC 8936): the transmitter holds a request until SETs are available,
    /// and the next request goes out as soon as a response was handled. Otherwise the transmitter
    /// is asked to answer at once and polled every <see cref="Interval"/>. Requires a transmitter
    /// that holds requests; one that answers at once is polled every <see cref="Interval"/>
    /// nevertheless.
    /// </summary>
    public bool LongPolling { get; set; }

    /// <summary>
    /// How long the transmitter holds a long poll request, part of the agreement with it. The
    /// request waits that long plus a margin for the response.
    /// </summary>
    public TimeSpan LongPollingHold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How the poller reacts when the transmitter rate-limits the poll endpoint.
    /// </summary>
    public SsfRateLimitOptions RateLimit { get; } = new();
}

/// <summary>
/// How the poller reacts when the transmitter rate-limits the poll endpoint.
/// </summary>
public sealed class SsfRateLimitOptions
{
    /// <summary>
    /// Pause after a <c>429 Too Many Requests</c> without a <c>Retry-After</c> header. Not set:
    /// poll again at the regular interval.
    /// </summary>
    public TimeSpan? FallbackBackoff { get; set; }

    /// <summary>
    /// Longest pause a <c>Retry-After</c> header or the fallback can cause.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// How SETs are validated.
/// </summary>
public sealed class SsfSetValidationOptions
{
    /// <summary>
    /// The algorithms accepted when <see cref="AcceptedAlgorithms"/> is left empty: <c>RS256</c>,
    /// as required by the CAEP interoperability profile.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAcceptedAlgorithms = ["RS256"];

    /// <summary>
    /// JWS algorithms accepted for SET signatures. Empty, the default, accepts
    /// <see cref="DefaultAcceptedAlgorithms"/>: configuration binding adds to a list, so the
    /// default is not in the list itself.
    /// </summary>
    public List<string> AcceptedAlgorithms { get; } = [];

    /// <summary>
    /// The algorithms accepted: <see cref="AcceptedAlgorithms"/>, or <see cref="DefaultAcceptedAlgorithms"/>
    /// if none are configured.
    /// </summary>
    public IReadOnlyList<string> EffectiveAcceptedAlgorithms => (AcceptedAlgorithms.Count > 0) ? AcceptedAlgorithms : DefaultAcceptedAlgorithms;

    /// <summary>
    /// Minimum RSA key size in bits for SET signing keys. Use 0 to disable the check.
    /// </summary>
    public int MinRsaKeySize { get; set; } = 2048;

    /// <summary>
    /// Whether the SET must be explicitly typed with a <c>typ</c> header of <c>secevent+jwt</c>.
    /// </summary>
    public bool RequireTypeHeader { get; set; } = true;

    /// <summary>
    /// How strictly the subject of a SET is validated: <c>Strict</c> requires the top-level
    /// <c>sub_id</c> claim of SSF 1.0, <c>Legacy</c> accepts SETs of transmitters following
    /// earlier drafts, which put the subject into the event payload.
    /// </summary>
    public SubjectCompatibilityMode SubjectCompatibility { get; set; } = SubjectCompatibilityMode.Strict;

    /// <summary>
    /// Tolerated clock skew when checking that a SET was not issued in the future.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// PUSH delivery (RFC 8935).
/// </summary>
public sealed class SsfPushOptions
{
    /// <summary>
    /// Exact value the transmitter must send in the <c>Authorization</c> header, for example
    /// <c>Bearer s3cr3t</c>. No header check is performed when not set.
    /// </summary>
    public string? ExpectedAuthHeader { get; set; }

    /// <summary>
    /// URL under which the transmitter reaches the push endpoint. Required for a stream managed by
    /// the receiver.
    /// </summary>
    public Uri? DeliveryEndpointUrl { get; set; }

    /// <summary>
    /// Path of the push endpoint of this application, <c>/ssf/push</c> by default. Shared by all
    /// transmitters, read from <c>EasySsf:Receiver:Push</c> only.
    /// </summary>
    public string EndpointPath { get; set; } = "/ssf/push";
}
