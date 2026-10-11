using EasySsf.Core.Event;
using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Configuration of the OpenID Shared Signals Framework (SSF) receiver, bound from the
/// <see cref="SsfReceiverDefaults.ConfigurationSection"/> section.
/// <para>
/// The settings of a transmitter (<see cref="SsfTransmitterOptions"/>) at <c>EasySsf:Receiver</c>
/// configure the <see cref="SsfTransmitter.DefaultName">default</see> transmitter;
/// <see cref="Transmitters"/> (<c>EasySsf:Receiver:Transmitters:&lt;name&gt;</c>) adds further
/// ones, each complete on its own. Everything else is shared by all transmitters.
/// </para>
/// </summary>
public sealed class SsfReceiverOptions : SsfTransmitterOptions
{
    /// <summary>
    /// Whether the SSF receiver is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Aliases for event type URIs, e.g. <c>AcmeLogin: https://events.acme.example/login</c>,
    /// usable wherever an event type is named. They add to the built-in aliases of the SSF, CAEP
    /// and RISC event types and cannot redefine one.
    /// </summary>
    public Dictionary<string, string> EventAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The members of a complex subject the application interprets. A SET with a member a
    /// transmitter declared critical (<c>critical_subject_members</c>) that is not listed here is
    /// rejected. Empty, the default, means the members <see cref="SsfSubject"/> gives access to
    /// (user, session, device, application, tenant, org_unit, group): configuration binding adds to
    /// a list, so the defaults are not in the list itself.
    /// </summary>
    public List<string> UnderstoodSubjectMembers { get; } = [];

    /// <summary>
    /// The members the application interprets: <see cref="UnderstoodSubjectMembers"/>, or
    /// <see cref="SsfSubject.Members"/> if none are configured.
    /// </summary>
    public IReadOnlyList<string> EffectiveUnderstoodSubjectMembers => (UnderstoodSubjectMembers.Count > 0) ? UnderstoodSubjectMembers : SsfSubject.Members;

    /// <summary>
    /// Further transmitters by name, each with the settings of a transmitter. The settings at
    /// <c>EasySsf:Receiver</c> configure the transmitter named <c>default</c>.
    /// </summary>
    public Dictionary<string, SsfTransmitterOptions> Transmitters { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Outbound HTTP calls to the transmitter.
    /// </summary>
    public SsfHttpOptions Http { get; } = new();

    /// <summary>
    /// Metrics.
    /// </summary>
    public SsfMetricsOptions Metrics { get; } = new();

    /// <summary>
    /// De-duplication of SETs by <c>jti</c>.
    /// </summary>
    public SsfDedupOptions Dedup { get; } = new();

    /// <summary>
    /// Revocation of access tokens and authentication cookies by security events.
    /// </summary>
    public SsfRevocationOptions Revocation { get; } = new();

    /// <summary>
    /// Termination of server-side sessions by security events.
    /// </summary>
    public SsfSessionTerminationOptions SessionTermination { get; } = new();

    /// <summary>
    /// The database stores of <c>EasySsf.Receiver.Data</c>, when the application added them.
    /// </summary>
    public SsfDataOptions Data { get; } = new();

    /// <summary>
    /// Whether the default transmitter is configured: an issuer is set at <c>EasySsf:Receiver</c>.
    /// </summary>
    public bool HasDefaultTransmitter => !string.IsNullOrWhiteSpace(TransmitterIssuer);

    /// <summary>
    /// The transmitters that are configured, by name: the default one if its issuer is set, and the
    /// named ones.
    /// </summary>
    /// <exception cref="SsfReceiverConfigurationException">
    /// if a named transmitter is called <c>default</c> while the default transmitter is configured
    /// as well, or has no name
    /// </exception>
    public IReadOnlyDictionary<string, SsfTransmitterOptions> ConfiguredTransmitters()
    {
        OrderedDictionary<string, SsfTransmitterOptions> configured = new(StringComparer.Ordinal);
        if (HasDefaultTransmitter)
        {
            configured[SsfTransmitter.DefaultName] = this;
        }
        foreach ((string name, SsfTransmitterOptions transmitter) in Transmitters)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SsfReceiverConfigurationException(SsfReceiverDefaults.ConfigurationSection + ":Transmitters", null,
                    "A transmitter under " + SsfReceiverDefaults.ConfigurationSection + ":Transmitters needs a name");
            }
            if (name == SsfTransmitter.DefaultName && HasDefaultTransmitter)
            {
                throw new SsfReceiverConfigurationException(SsfReceiverDefaults.ConfigurationSection + ":Transmitters", name,
                    $"The transmitter named '{SsfTransmitter.DefaultName}' is the one configured at {SsfReceiverDefaults.ConfigurationSection},"
                        + $" it cannot be configured under {SsfReceiverDefaults.ConfigurationSection}:Transmitters as well");
            }
            configured[name] = transmitter;
        }
        return configured;
    }
}

/// <summary>
/// Outbound HTTP calls to the transmitter.
/// </summary>
public sealed class SsfHttpOptions
{
    /// <summary>
    /// Timeout for calls to the transmitter, 5 seconds by default. A long poll waits for its hold
    /// time plus a margin regardless.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <c>User-Agent</c> header for calls to the transmitter. None is sent when not set.
    /// </summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Metrics of the receiver.
/// </summary>
public sealed class SsfMetricsOptions
{
    /// <summary>
    /// Whether received SETs and polls are recorded with <c>System.Diagnostics.Metrics</c>. The
    /// <c>IMeterFactory</c> this needs comes with the HTTP client factory, so metrics are on unless
    /// switched off here.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The database stores of <c>EasySsf.Receiver.Data</c>, which keep processed SETs, revocations and
/// the acknowledgements a poller owes in the database of the application, shared by all its
/// instances. They take effect when the application adds them with <c>AddDatabaseStores()</c>.
/// </summary>
public sealed class SsfDataOptions
{
    /// <summary>
    /// Whether the database stores are used. <see langword="false"/> keeps the state of the receiver
    /// in memory although the stores were added, for example in a test environment.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When to create the tables if they do not exist: for embedded databases (SQLite) only, which
    /// is the default, always, or never.
    /// </summary>
    public SsfSchemaInitialization InitializeSchema { get; set; } = SsfSchemaInitialization.Embedded;

    /// <summary>
    /// Prefix of the names of the tables, <c>EASYSSF_</c> by default. Letters, digits, <c>_</c> and
    /// <c>.</c> (for a schema) only.
    /// </summary>
    public string TablePrefix { get; set; } = "EASYSSF_";

    /// <summary>
    /// How often expired rows (processed SETs past <c>Dedup:Retention</c>, revocations past
    /// <c>Revocation:Ttl</c>, acknowledgements past <see cref="AckRetention"/>) are purged. The
    /// stores also purge when they are written to; zero turns the periodic cleanup off. 15 minutes
    /// by default.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the store keeps an acknowledgement a polling receiver owes its transmitter if no
    /// poll request manages to deliver it. 7 days by default.
    /// </summary>
    public TimeSpan AckRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How many acknowledgements one DELETE of the store removes at once; databases limit the
    /// parameters of a statement. 100 by default.
    /// </summary>
    public int AckDeleteBatchSize { get; set; } = 100;
}

/// <summary>
/// When the database stores create their tables.
/// </summary>
public enum SsfSchemaInitialization
{
    /// <summary>
    /// Only in an embedded database (SQLite), which has no migration tool of its own.
    /// </summary>
    Embedded,

    /// <summary>
    /// Whenever a table is missing, and add the columns of this release to a table of an older one.
    /// </summary>
    Always,

    /// <summary>
    /// Never: the application creates the tables, with the SQL that ships with the stores. A missing
    /// or outdated table fails the start with the statements to run.
    /// </summary>
    Never,
}

/// <summary>
/// De-duplication of SETs by <c>jti</c>.
/// </summary>
public sealed class SsfDedupOptions
{
    /// <summary>
    /// Whether SETs that were already processed are skipped.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of SET identifiers the in-memory store remembers.
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>
    /// How long a durable store remembers a processed SET. Has to cover the time the transmitter
    /// keeps trying to deliver a SET.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a SET counts as being handled by the instance that claimed it. Another instance
    /// that receives the SET in that time leaves it for a redelivery; after it, the claim counts as
    /// abandoned by a crashed instance and the SET is handled again. Longer than the longest event
    /// handler.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Revocation of access tokens and authentication cookies by security events: the
/// <c>ISsfTokenRevocationStore</c> and the handler that fills it. A JwtBearer scheme with
/// <c>UseSsfTokenRevocation()</c> and a cookie scheme with <c>UseSsfSessionRevocation()</c> consult
/// the store. The Java starter calls this the resource server integration.
/// </summary>
public sealed class SsfRevocationOptions
{
    /// <summary>
    /// The event types that revoke sessions when <see cref="SessionEventTypes"/> is left empty:
    /// <c>CaepSessionRevoked</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSessionEventTypes = ["CaepSessionRevoked"];

    /// <summary>
    /// Whether revocations are recorded and tokens of revoked sessions and subjects rejected.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Event types (alias or URI) that revoke the session they name, or all tokens of the user issued
    /// up to the event if they name none. Empty, the default, means
    /// <see cref="DefaultSessionEventTypes"/>.
    /// </summary>
    public List<string> SessionEventTypes { get; } = [];

    /// <summary>
    /// Event types (alias or URI) that revoke all tokens of the user they name, issued up to the
    /// event, whether or not the event names a session; for example <c>CaepCredentialChange</c> for
    /// an application whose sessions are cookies. None by default.
    /// </summary>
    public List<string> UserEventTypes { get; } = [];

    /// <summary>
    /// How long a revocation is remembered. Must be at least the maximum lifetime of the access
    /// tokens and cookies it guards, or a revoked token becomes usable again when the revocation is
    /// forgotten. 24 hours by default; <c>AddSsfSessionRevocation()</c> refuses a cookie scheme whose
    /// <c>ExpireTimeSpan</c> is longer.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The session event types: <see cref="SessionEventTypes"/>, or <see cref="DefaultSessionEventTypes"/>
    /// if none are configured.
    /// </summary>
    public IReadOnlyList<string> EffectiveSessionEventTypes => (SessionEventTypes.Count > 0) ? SessionEventTypes : DefaultSessionEventTypes;
}

/// <summary>
/// Termination of server-side sessions by security events, through the
/// <c>ISsfSessionTerminator</c> the application registers. Without one, nothing is terminated:
/// stateless authentication cookies are rejected by the revocation check instead. The Java starter
/// calls this the OIDC client integration.
/// </summary>
public sealed class SsfSessionTerminationOptions
{
    /// <summary>
    /// The event types that terminate sessions when <see cref="SessionEventTypes"/> is left empty:
    /// <c>CaepSessionRevoked</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSessionEventTypes = ["CaepSessionRevoked"];

    /// <summary>
    /// The event types that terminate the sessions of a user when <see cref="UserEventTypes"/> is
    /// left empty: <c>CaepCredentialChange</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultUserEventTypes = ["CaepCredentialChange"];

    /// <summary>
    /// Whether local sessions are terminated when a matching event is received and an
    /// <c>ISsfSessionTerminator</c> is registered.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Event types (alias or URI) that terminate the sessions identified by the event's subject
    /// (session id and / or user). Empty, the default, means <see cref="DefaultSessionEventTypes"/>.
    /// </summary>
    public List<string> SessionEventTypes { get; } = [];

    /// <summary>
    /// Event types (alias or URI) that terminate all sessions of the user identified by the event's
    /// subject. Empty, the default, means <see cref="DefaultUserEventTypes"/>.
    /// </summary>
    public List<string> UserEventTypes { get; } = [];

    /// <summary>
    /// Attributes of the <c>scim</c> subject of a SCIM Event (RFC 9967) and the claim of the
    /// logged-in user each is compared with, e.g. <c>externalId: sub</c>. A multi-valued attribute
    /// such as <c>emails</c> matches if any of its values does. The subject matches if any pair does.
    /// Empty, the default, means <c>SsfSubjectClaimsMatcher.DefaultScimAttributeClaims</c>.
    /// </summary>
    public Dictionary<string, string> ScimAttributeClaims { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> EffectiveSessionEventTypes => (SessionEventTypes.Count > 0) ? SessionEventTypes : DefaultSessionEventTypes;

    public IReadOnlyList<string> EffectiveUserEventTypes => (UserEventTypes.Count > 0) ? UserEventTypes : DefaultUserEventTypes;

    /// <summary>
    /// The attribute-claim pairs a <c>scim</c> subject is matched by: <see cref="ScimAttributeClaims"/>,
    /// or the defaults of <c>SsfSubjectClaimsMatcher</c> if none are configured.
    /// </summary>
    public IReadOnlyDictionary<string, string> EffectiveScimAttributeClaims
        => (ScimAttributeClaims.Count > 0) ? ScimAttributeClaims : Receiver.Session.SsfSubjectClaimsMatcher.DefaultScimAttributeClaims;
}
