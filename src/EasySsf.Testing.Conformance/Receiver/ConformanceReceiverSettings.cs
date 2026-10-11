using EasySsf.Core;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// The settings of a receiver under test: the transmitter to use, how to authenticate at it, the
/// delivery, the stream and the limits of a run. An application binds them from its configuration
/// (the receiver under test of this repository at <c>Cts</c>) or sets them in code. Lists start
/// empty because configuration binding adds to a list; the <c>Effective...</c> properties resolve
/// the defaults.
/// </summary>
public sealed class ConformanceReceiverSettings
{
    public TransmitterSettings Transmitter { get; } = new();

    public AuthSettings Auth { get; } = new();

    public DeliverySettings Delivery { get; } = new();

    public StreamSettings Stream { get; } = new();

    public RunSettings Run { get; } = new();

    /// <summary>
    /// The emulated transmitter.
    /// </summary>
    public sealed class TransmitterSettings
    {
        /// <summary>
        /// Issuer of the emulated transmitter: the URL of the test instance in the conformance
        /// suite, with its alias, e.g. <c>https://localhost.emobix.co.uk:8443/test/a/easyssf-receiver</c>.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// A PEM file with the certificate of a conformance suite with a self-signed certificate,
        /// which the receiver then trusts whatever host name it is presented for.
        /// </summary>
        public string? CertificateFile { get; set; }

        /// <summary>
        /// Whether to trust any TLS certificate of the transmitter instead. Only for a conformance
        /// suite running locally.
        /// </summary>
        public bool TrustAllCertificates { get; set; }
    }

    /// <summary>
    /// How the receiver authenticates at the transmitter.
    /// </summary>
    public sealed class AuthSettings
    {
        /// <summary>
        /// The scopes requested when <see cref="Scopes"/> is left empty.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultScopes = ["ssf.read", "ssf.manage"];

        /// <summary>
        /// <see cref="ConformanceAuthMode.Dynamic"/> obtains an access token with the client credentials
        /// grant from the token endpoint of the emulated transmitter, <see cref="ConformanceAuthMode.Static"/>
        /// sends the configured access token. Matches the 'Authentication Variant' of the test plan.
        /// </summary>
        public ConformanceAuthMode Mode { get; set; } = ConformanceAuthMode.Dynamic;

        /// <summary>
        /// Client id, as configured in the test plan (<c>client.client_id</c>).
        /// </summary>
        public string ClientId { get; set; } = "easyssf-receiver";

        /// <summary>
        /// Client secret, as configured in the test plan (<c>client.client_secret</c>).
        /// </summary>
        public string ClientSecret { get; set; } = "easyssf-receiver-secret";

        /// <summary>
        /// Scopes to request, as configured in the test plan (<c>client.scope</c>). Empty means
        /// <see cref="DefaultScopes"/>.
        /// </summary>
        public List<string> Scopes { get; } = [];

        public IReadOnlyList<string> EffectiveScopes => (Scopes.Count > 0) ? Scopes : DefaultScopes;

        /// <summary>
        /// How the client authenticates at the token endpoint. Matches the 'Client Authentication
        /// Type' of the test plan.
        /// </summary>
        public ConformanceClientAuthenticationMethod ClientAuthenticationMethod { get; set; } = ConformanceClientAuthenticationMethod.Basic;

        /// <summary>
        /// Access token to send in mode <see cref="ConformanceAuthMode.Static"/>, as configured in the
        /// test plan (<c>ssf.transmitter.access_token</c>).
        /// </summary>
        public string? AccessToken { get; set; }
    }

    /// <summary>
    /// How the transmitter delivers.
    /// </summary>
    public sealed class DeliverySettings
    {
        /// <summary>
        /// Push or poll. Matches the 'SSF Delivery Mode' of the test plan.
        /// </summary>
        public SsfDeliveryMethod Method { get; set; } = SsfDeliveryMethod.Push;

        /// <summary>
        /// URL under which the conformance suite reaches the push endpoint of this application. Must
        /// be <c>https</c>.
        /// </summary>
        public Uri PushUrl { get; set; } = new("https://localhost:9443/ssf/push");

        /// <summary>
        /// How often the transmitter is polled with poll delivery.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// The stream the receiver creates.
    /// </summary>
    public sealed class StreamSettings
    {
        /// <summary>
        /// The event types requested when <see cref="EventsRequested"/> is left empty.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultEventsRequested = ["CaepSessionRevoked", "CaepCredentialChange", "CaepDeviceComplianceChange"];

        /// <summary>
        /// The subject removed when <see cref="SubjectToRemove"/> is left empty: the e-mail subject
        /// of the suite configurations.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> DefaultSubjectToRemove = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "email",
            ["email"] = "foo@example.com",
        };

        /// <summary>
        /// Event types (alias or URI) the stream requests. Empty means <see cref="DefaultEventsRequested"/>.
        /// </summary>
        public List<string> EventsRequested { get; } = [];

        public IReadOnlyList<string> EffectiveEventsRequested => (EventsRequested.Count > 0) ? EventsRequested : DefaultEventsRequested;

        /// <summary>
        /// Subject the <see cref="ConformanceScenario.RemoveSubject"/> scenario removes from the
        /// stream, as its members, e.g. one of the subjects listed in the 'SSF valid SubjectId' field
        /// of the test plan. Empty means <see cref="DefaultSubjectToRemove"/>.
        /// </summary>
        public Dictionary<string, string> SubjectToRemove { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> EffectiveSubjectToRemove => (SubjectToRemove.Count > 0) ? SubjectToRemove : DefaultSubjectToRemove;
    }

    /// <summary>
    /// The limits of a run.
    /// </summary>
    public sealed class RunSettings
    {
        /// <summary>
        /// How long to wait for the verification event before going on without it.
        /// </summary>
        public TimeSpan VerificationTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The stream is deleted once no SET arrived for this long. Has to exceed the pauses the
        /// tests make between events (20 seconds in the access token expiry test, 15 seconds in the
        /// transmitter-initiated status change test).
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// A run ends at the latest after this time.
        /// </summary>
        public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(5);
    }
}

/// <summary>
/// How the receiver under test obtains its access token.
/// </summary>
public enum ConformanceAuthMode
{
    /// <summary>
    /// The client credentials grant at the token endpoint of the emulated transmitter.
    /// </summary>
    Dynamic,

    /// <summary>
    /// A configured access token.
    /// </summary>
    Static,
}

/// <summary>
/// How the client authenticates at the token endpoint.
/// </summary>
public enum ConformanceClientAuthenticationMethod
{
    /// <summary>
    /// HTTP basic authentication (<c>client_secret_basic</c>).
    /// </summary>
    Basic,

    /// <summary>
    /// Credentials in the request body (<c>client_secret_post</c>).
    /// </summary>
    Post,
}
