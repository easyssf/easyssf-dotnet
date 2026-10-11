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
