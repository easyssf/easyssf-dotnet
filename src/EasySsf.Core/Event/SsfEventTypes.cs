using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace EasySsf.Core.Event;

/// <summary>
/// Event type URIs defined by OpenID SSF, CAEP and RISC, and by SCIM Events (RFC 9967), together
/// with short aliases (for example <c>CaepSessionRevoked</c>) that can be used wherever an event
/// type is expected.
/// </summary>
public static class SsfEventTypes
{
    private const string Ssf = "https://schemas.openid.net/secevent/ssf/event-type/";

    private const string Caep = "https://schemas.openid.net/secevent/caep/event-type/";

    private const string Risc = "https://schemas.openid.net/secevent/risc/event-type/";

    private const string Scim = "urn:ietf:params:scim:event:";

    public const string SsfStreamVerification = Ssf + "verification";

    public const string SsfStreamUpdated = Ssf + "stream-updated";

    public const string CaepSessionRevoked = Caep + "session-revoked";

    public const string CaepTokenClaimsChange = Caep + "token-claims-change";

    public const string CaepCredentialChange = Caep + "credential-change";

    public const string CaepAssuranceLevelChange = Caep + "assurance-level-change";

    public const string CaepDeviceComplianceChange = Caep + "device-compliance-change";

    public const string CaepSessionEstablished = Caep + "session-established";

    public const string CaepSessionPresented = Caep + "session-presented";

    public const string CaepRiskLevelChange = Caep + "risk-level-change";

    public const string RiscAccountCredentialChangeRequired = Risc + "account-credential-change-required";

    public const string RiscAccountPurged = Risc + "account-purged";

    public const string RiscAccountDisabled = Risc + "account-disabled";

    public const string RiscAccountEnabled = Risc + "account-enabled";

    public const string RiscIdentifierChanged = Risc + "identifier-changed";

    public const string RiscIdentifierRecycled = Risc + "identifier-recycled";

    public const string RiscCredentialCompromise = Risc + "credential-compromise";

    public const string RiscOptIn = Risc + "opt-in";

    public const string RiscOptOutInitiated = Risc + "opt-out-initiated";

    public const string RiscOptOutCancelled = Risc + "opt-out-cancelled";

    public const string RiscOptOutEffective = Risc + "opt-out-effective";

    public const string RiscRecoveryActivated = Risc + "recovery-activated";

    public const string RiscRecoveryInformationChanged = Risc + "recovery-information-changed";

    /// <summary>
    /// RISC 1.0, section 2.11: all sessions of the account were revoked. Deprecated by RISC in
    /// favour of <see cref="CaepSessionRevoked"/>, but still emitted by some transmitters.
    /// </summary>
    public const string RiscSessionsRevoked = Risc + "sessions-revoked";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.3.1): the resource was added to the event feed.
    /// </summary>
    public const string ScimFeedAdd = Scim + "feed:add";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.3.2): the resource was removed from the event feed.
    /// </summary>
    public const string ScimFeedRemove = Scim + "feed:remove";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.1): a resource was created, the payload lists the
    /// <c>attributes</c> set.
    /// </summary>
    public const string ScimProvCreateNotice = Scim + "prov:create:notice";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.1): a resource was created, the payload carries its
    /// representation as <c>data</c>.
    /// </summary>
    public const string ScimProvCreateFull = Scim + "prov:create:full";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.2): a resource was modified with SCIM PATCH, the payload
    /// lists the <c>attributes</c> modified.
    /// </summary>
    public const string ScimProvPatchNotice = Scim + "prov:patch:notice";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.2): a resource was modified with SCIM PATCH, the payload
    /// carries the patch operations as <c>data</c>.
    /// </summary>
    public const string ScimProvPatchFull = Scim + "prov:patch:full";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.3): a resource was replaced with SCIM PUT, the payload
    /// lists the <c>attributes</c> modified.
    /// </summary>
    public const string ScimProvPutNotice = Scim + "prov:put:notice";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.3): a resource was replaced with SCIM PUT, the payload
    /// carries the new representation as <c>data</c>.
    /// </summary>
    public const string ScimProvPutFull = Scim + "prov:put:full";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.4): the resource was deleted.
    /// </summary>
    public const string ScimProvDelete = Scim + "prov:delete";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.5): the resource was activated.
    /// </summary>
    public const string ScimProvActivate = Scim + "prov:activate";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.4.6): the resource was deactivated, typically its user may
    /// no longer have an active session.
    /// </summary>
    public const string ScimProvDeactivate = Scim + "prov:deactivate";

    /// <summary>
    /// SCIM Events (RFC 9967, section 2.5.1.3): an asynchronous SCIM request completed.
    /// </summary>
    public const string ScimMiscAsyncResponse = Scim + "misc:asyncresp";

    private static readonly ConcurrentDictionary<string, string> UriByAlias = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, string> AliasByUri = new(StringComparer.Ordinal);

    private static readonly Lock RegistrationLock = new();

    static SsfEventTypes()
    {
        Register("SsfStreamVerification", SsfStreamVerification);
        Register("SsfStreamUpdated", SsfStreamUpdated);
        Register("CaepSessionRevoked", CaepSessionRevoked);
        Register("CaepTokenClaimsChange", CaepTokenClaimsChange);
        Register("CaepCredentialChange", CaepCredentialChange);
        Register("CaepAssuranceLevelChange", CaepAssuranceLevelChange);
        Register("CaepDeviceComplianceChange", CaepDeviceComplianceChange);
        Register("CaepSessionEstablished", CaepSessionEstablished);
        Register("CaepSessionPresented", CaepSessionPresented);
        Register("CaepRiskLevelChange", CaepRiskLevelChange);
        Register("RiscAccountCredentialChangeRequired", RiscAccountCredentialChangeRequired);
        Register("RiscAccountPurged", RiscAccountPurged);
        Register("RiscAccountDisabled", RiscAccountDisabled);
        Register("RiscAccountEnabled", RiscAccountEnabled);
        Register("RiscIdentifierChanged", RiscIdentifierChanged);
        Register("RiscIdentifierRecycled", RiscIdentifierRecycled);
        Register("RiscCredentialCompromise", RiscCredentialCompromise);
        Register("RiscOptIn", RiscOptIn);
        Register("RiscOptOutInitiated", RiscOptOutInitiated);
        Register("RiscOptOutCancelled", RiscOptOutCancelled);
        Register("RiscOptOutEffective", RiscOptOutEffective);
        Register("RiscRecoveryActivated", RiscRecoveryActivated);
        Register("RiscRecoveryInformationChanged", RiscRecoveryInformationChanged);
        Register("RiscSessionsRevoked", RiscSessionsRevoked);
        Register("ScimFeedAdd", ScimFeedAdd);
        Register("ScimFeedRemove", ScimFeedRemove);
        Register("ScimProvCreateNotice", ScimProvCreateNotice);
        Register("ScimProvCreateFull", ScimProvCreateFull);
        Register("ScimProvPatchNotice", ScimProvPatchNotice);
        Register("ScimProvPatchFull", ScimProvPatchFull);
        Register("ScimProvPutNotice", ScimProvPutNotice);
        Register("ScimProvPutFull", ScimProvPutFull);
        Register("ScimProvDelete", ScimProvDelete);
        Register("ScimProvActivate", ScimProvActivate);
        Register("ScimProvDeactivate", ScimProvDeactivate);
        Register("ScimMiscAsyncResponse", ScimMiscAsyncResponse);
    }

    private static void Register(string alias, string uri)
    {
        UriByAlias[alias] = uri;
        AliasByUri[uri] = alias;
    }

    /// <summary>
    /// Registers an alias for an event type URI, for example of a vendor specific event type, so
    /// that it can be used wherever an event type is named: in handlers, in the events requested
    /// of a stream, in configuration. Aliases are a convenience of easyssf, the URIs stay the
    /// canonical names.
    /// <para>
    /// Aliases do not conflict: an alias cannot be redefined to another URI, neither a built-in
    /// one nor one registered before. A URI may have several aliases; the first one registered is
    /// the one <see cref="AliasOf"/> returns. Registering a mapping that exists already does
    /// nothing.
    /// </para>
    /// </summary>
    /// <param name="alias">
    /// the alias, a name without <c>:</c> or <c>/</c> so that it cannot be mistaken for a URI
    /// </param>
    /// <param name="uri">the absolute event type URI</param>
    /// <exception cref="ArgumentException">
    /// if the alias or the URI is not well-formed, or the alias is already mapped to another URI
    /// </exception>
    public static void RegisterAlias(string alias, string uri)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Contains(':') || alias.Contains('/') || alias != alias.Trim())
        {
            throw new ArgumentException(
                $"An event type alias must be a name without ':' or '/' and surrounding whitespace: '{alias}'",
                nameof(alias));
        }
        if (string.IsNullOrWhiteSpace(uri) || !uri.Contains(':') || uri != uri.Trim())
        {
            throw new ArgumentException($"An event type must be an absolute URI: '{uri}'", nameof(uri));
        }
        lock (RegistrationLock)
        {
            if (UriByAlias.TryGetValue(alias, out string? existing) && existing != uri)
            {
                throw new ArgumentException(
                    $"The event type alias '{alias}' is already mapped to {existing}, not to {uri}", nameof(alias));
            }
            UriByAlias[alias] = uri;
            AliasByUri.TryAdd(uri, alias);
        }
    }

    /// <summary>
    /// The known aliases and their event type URIs, built-in and registered, sorted by alias.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Aliases()
    {
        return new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(UriByAlias, StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether the name is a known alias.
    /// </summary>
    public static bool IsAlias(string? name)
    {
        return name is not null && UriByAlias.ContainsKey(name);
    }

    /// <summary>
    /// Whether the event type is in the CAEP namespace
    /// (<c>https://schemas.openid.net/secevent/caep/event-type/</c>), one of the events of CAEP 1.0
    /// or a later one.
    /// </summary>
    public static bool IsCaepEvent(string? aliasOrUri)
    {
        return aliasOrUri is not null && Resolve(aliasOrUri).StartsWith(Caep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the event type is in the RISC namespace
    /// (<c>https://schemas.openid.net/secevent/risc/event-type/</c>), one of the events of RISC 1.0
    /// or a later one.
    /// </summary>
    public static bool IsRiscEvent(string? aliasOrUri)
    {
        return aliasOrUri is not null && Resolve(aliasOrUri).StartsWith(Risc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves an alias to its event type URI. Anything that is not a known alias is returned
    /// unchanged, so full URIs (including vendor specific ones) pass through.
    /// </summary>
    public static string Resolve(string aliasOrUri)
    {
        ArgumentNullException.ThrowIfNull(aliasOrUri);
        return UriByAlias.TryGetValue(aliasOrUri, out string? uri) ? uri : aliasOrUri;
    }

    /// <summary>
    /// Returns the alias of the given event type URI, or the URI itself if it has none.
    /// </summary>
    public static string AliasOf(string eventTypeUri)
    {
        ArgumentNullException.ThrowIfNull(eventTypeUri);
        return AliasByUri.TryGetValue(eventTypeUri, out string? alias) ? alias : eventTypeUri;
    }
}
