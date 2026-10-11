using EasySsf.Core.Event;

namespace EasySsf.Core.Caep;

/// <summary>
/// The events of CAEP 1.0, section 3, and <see cref="Other"/> for an event type in the CAEP
/// namespace this version does not know. New members are added as CAEP defines events, so do not
/// switch over this enum exhaustively; derive from <c>SsfCaepEventHandler</c> instead.
/// </summary>
public enum SsfCaepEventKind
{
    /// <summary>
    /// The session was revoked (section 3.1).
    /// </summary>
    SessionRevoked,

    /// <summary>
    /// Claims of the token the subject identifies changed (section 3.2).
    /// </summary>
    TokenClaimsChange,

    /// <summary>
    /// A credential was created, revoked, updated or deleted (section 3.3).
    /// </summary>
    CredentialChange,

    /// <summary>
    /// The assurance level of the subject changed (section 3.4).
    /// </summary>
    AssuranceLevelChange,

    /// <summary>
    /// The compliance status of the device changed (section 3.5).
    /// </summary>
    DeviceComplianceChange,

    /// <summary>
    /// A session was established (section 3.6).
    /// </summary>
    SessionEstablished,

    /// <summary>
    /// A session was presented to the transmitter (section 3.7).
    /// </summary>
    SessionPresented,

    /// <summary>
    /// The risk level of the subject changed (section 3.8).
    /// </summary>
    RiskLevelChange,

    /// <summary>
    /// An event type in the CAEP namespace this version does not know, for example one of a later
    /// CAEP version. The common claims of CAEP section 2 are still available.
    /// </summary>
    Other,
}

/// <summary>
/// The event type URIs of the <see cref="SsfCaepEventKind"/>s.
/// </summary>
public static class SsfCaepEventKinds
{
    private static readonly IReadOnlyDictionary<string, SsfCaepEventKind> ByEventType = new Dictionary<string, SsfCaepEventKind>(StringComparer.Ordinal)
    {
        [SsfEventTypes.CaepSessionRevoked] = SsfCaepEventKind.SessionRevoked,
        [SsfEventTypes.CaepTokenClaimsChange] = SsfCaepEventKind.TokenClaimsChange,
        [SsfEventTypes.CaepCredentialChange] = SsfCaepEventKind.CredentialChange,
        [SsfEventTypes.CaepAssuranceLevelChange] = SsfCaepEventKind.AssuranceLevelChange,
        [SsfEventTypes.CaepDeviceComplianceChange] = SsfCaepEventKind.DeviceComplianceChange,
        [SsfEventTypes.CaepSessionEstablished] = SsfCaepEventKind.SessionEstablished,
        [SsfEventTypes.CaepSessionPresented] = SsfCaepEventKind.SessionPresented,
        [SsfEventTypes.CaepRiskLevelChange] = SsfCaepEventKind.RiskLevelChange,
    };

    /// <summary>
    /// The event type URI of the kind, <see langword="null"/> for <see cref="SsfCaepEventKind.Other"/>.
    /// </summary>
    public static string? EventType(this SsfCaepEventKind kind)
    {
        return kind switch
        {
            SsfCaepEventKind.SessionRevoked => SsfEventTypes.CaepSessionRevoked,
            SsfCaepEventKind.TokenClaimsChange => SsfEventTypes.CaepTokenClaimsChange,
            SsfCaepEventKind.CredentialChange => SsfEventTypes.CaepCredentialChange,
            SsfCaepEventKind.AssuranceLevelChange => SsfEventTypes.CaepAssuranceLevelChange,
            SsfCaepEventKind.DeviceComplianceChange => SsfEventTypes.CaepDeviceComplianceChange,
            SsfCaepEventKind.SessionEstablished => SsfEventTypes.CaepSessionEstablished,
            SsfCaepEventKind.SessionPresented => SsfEventTypes.CaepSessionPresented,
            SsfCaepEventKind.RiskLevelChange => SsfEventTypes.CaepRiskLevelChange,
            _ => null,
        };
    }

    /// <param name="eventType">an event type URI or alias</param>
    /// <returns>
    /// the kind of CAEP event, <see cref="SsfCaepEventKind.Other"/> for an unknown event type in the
    /// CAEP namespace, <see langword="null"/> if the event type is not a CAEP event
    /// </returns>
    public static SsfCaepEventKind? Of(string? eventType)
    {
        if (!SsfEventTypes.IsCaepEvent(eventType))
        {
            return null;
        }
        return ByEventType.TryGetValue(SsfEventTypes.Resolve(eventType!), out SsfCaepEventKind kind) ? kind : SsfCaepEventKind.Other;
    }
}
