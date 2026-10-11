using EasySsf.Core.Event;

namespace EasySsf.Core.Risc;

/// <summary>
/// The events of RISC 1.0, section 2, and <see cref="Other"/> for an event type in the RISC
/// namespace this version does not know. New members are added as RISC defines events, so do not
/// switch over this enum exhaustively; derive from <c>SsfRiscEventHandler</c> instead.
/// </summary>
public enum SsfRiscEventKind
{
    /// <summary>
    /// The account has to change its credential, for example after a leak elsewhere (section 2.1).
    /// </summary>
    AccountCredentialChangeRequired,

    /// <summary>
    /// The account was permanently deleted (section 2.2).
    /// </summary>
    AccountPurged,

    /// <summary>
    /// The account was disabled, see <see cref="SsfRiscEvent.Reason"/> (section 2.3).
    /// </summary>
    AccountDisabled,

    /// <summary>
    /// The account was enabled again (section 2.4).
    /// </summary>
    AccountEnabled,

    /// <summary>
    /// The identifier of the subject, an email address or phone number, changed to
    /// <see cref="SsfRiscEvent.NewValue"/> (section 2.5).
    /// </summary>
    IdentifierChanged,

    /// <summary>
    /// The identifier was given to someone else (section 2.6).
    /// </summary>
    IdentifierRecycled,

    /// <summary>
    /// A credential of the subject was found compromised, see
    /// <see cref="SsfRiscEvent.CredentialType"/> (section 2.7).
    /// </summary>
    CredentialCompromise,

    /// <summary>
    /// The account takes part in the RISC exchange (section 2.8.1).
    /// </summary>
    OptIn,

    /// <summary>
    /// The user asked to leave the RISC exchange (section 2.8.2).
    /// </summary>
    OptOutInitiated,

    /// <summary>
    /// The user withdrew the request to leave (section 2.8.3).
    /// </summary>
    OptOutCancelled,

    /// <summary>
    /// The account left the RISC exchange (section 2.8.4).
    /// </summary>
    OptOutEffective,

    /// <summary>
    /// The account went through a recovery flow (section 2.9).
    /// </summary>
    RecoveryActivated,

    /// <summary>
    /// The recovery information of the account changed (section 2.10).
    /// </summary>
    RecoveryInformationChanged,

    /// <summary>
    /// All sessions of the account were revoked (section 2.11). Deprecated by RISC in favour of
    /// CAEP's <c>session-revoked</c>, still sent by some transmitters.
    /// </summary>
    SessionsRevoked,

    /// <summary>
    /// An event type in the RISC namespace this version does not know.
    /// </summary>
    Other,
}

/// <summary>
/// The event type URIs of the <see cref="SsfRiscEventKind"/>s.
/// </summary>
public static class SsfRiscEventKinds
{
    private static readonly IReadOnlyDictionary<string, SsfRiscEventKind> ByEventType = new Dictionary<string, SsfRiscEventKind>(StringComparer.Ordinal)
    {
        [SsfEventTypes.RiscAccountCredentialChangeRequired] = SsfRiscEventKind.AccountCredentialChangeRequired,
        [SsfEventTypes.RiscAccountPurged] = SsfRiscEventKind.AccountPurged,
        [SsfEventTypes.RiscAccountDisabled] = SsfRiscEventKind.AccountDisabled,
        [SsfEventTypes.RiscAccountEnabled] = SsfRiscEventKind.AccountEnabled,
        [SsfEventTypes.RiscIdentifierChanged] = SsfRiscEventKind.IdentifierChanged,
        [SsfEventTypes.RiscIdentifierRecycled] = SsfRiscEventKind.IdentifierRecycled,
        [SsfEventTypes.RiscCredentialCompromise] = SsfRiscEventKind.CredentialCompromise,
        [SsfEventTypes.RiscOptIn] = SsfRiscEventKind.OptIn,
        [SsfEventTypes.RiscOptOutInitiated] = SsfRiscEventKind.OptOutInitiated,
        [SsfEventTypes.RiscOptOutCancelled] = SsfRiscEventKind.OptOutCancelled,
        [SsfEventTypes.RiscOptOutEffective] = SsfRiscEventKind.OptOutEffective,
        [SsfEventTypes.RiscRecoveryActivated] = SsfRiscEventKind.RecoveryActivated,
        [SsfEventTypes.RiscRecoveryInformationChanged] = SsfRiscEventKind.RecoveryInformationChanged,
        [SsfEventTypes.RiscSessionsRevoked] = SsfRiscEventKind.SessionsRevoked,
    };

    /// <summary>
    /// The event type URI of the kind, <see langword="null"/> for <see cref="SsfRiscEventKind.Other"/>.
    /// </summary>
    public static string? EventType(this SsfRiscEventKind kind)
    {
        return kind switch
        {
            SsfRiscEventKind.AccountCredentialChangeRequired => SsfEventTypes.RiscAccountCredentialChangeRequired,
            SsfRiscEventKind.AccountPurged => SsfEventTypes.RiscAccountPurged,
            SsfRiscEventKind.AccountDisabled => SsfEventTypes.RiscAccountDisabled,
            SsfRiscEventKind.AccountEnabled => SsfEventTypes.RiscAccountEnabled,
            SsfRiscEventKind.IdentifierChanged => SsfEventTypes.RiscIdentifierChanged,
            SsfRiscEventKind.IdentifierRecycled => SsfEventTypes.RiscIdentifierRecycled,
            SsfRiscEventKind.CredentialCompromise => SsfEventTypes.RiscCredentialCompromise,
            SsfRiscEventKind.OptIn => SsfEventTypes.RiscOptIn,
            SsfRiscEventKind.OptOutInitiated => SsfEventTypes.RiscOptOutInitiated,
            SsfRiscEventKind.OptOutCancelled => SsfEventTypes.RiscOptOutCancelled,
            SsfRiscEventKind.OptOutEffective => SsfEventTypes.RiscOptOutEffective,
            SsfRiscEventKind.RecoveryActivated => SsfEventTypes.RiscRecoveryActivated,
            SsfRiscEventKind.RecoveryInformationChanged => SsfEventTypes.RiscRecoveryInformationChanged,
            SsfRiscEventKind.SessionsRevoked => SsfEventTypes.RiscSessionsRevoked,
            _ => null,
        };
    }

    /// <summary>
    /// Whether this is one of the opt-in and opt-out events of section 2.8, which report the
    /// participation of the account in the exchange rather than a risk.
    /// </summary>
    public static bool IsOptOutState(this SsfRiscEventKind kind)
    {
        return kind is SsfRiscEventKind.OptIn or SsfRiscEventKind.OptOutInitiated or SsfRiscEventKind.OptOutCancelled
            or SsfRiscEventKind.OptOutEffective;
    }

    /// <param name="eventType">an event type URI or alias</param>
    /// <returns>
    /// the kind of RISC event, <see cref="SsfRiscEventKind.Other"/> for an unknown event type in the
    /// RISC namespace, <see langword="null"/> if the event type is not a RISC event
    /// </returns>
    public static SsfRiscEventKind? Of(string? eventType)
    {
        if (!SsfEventTypes.IsRiscEvent(eventType))
        {
            return null;
        }
        return ByEventType.TryGetValue(SsfEventTypes.Resolve(eventType!), out SsfRiscEventKind kind) ? kind : SsfRiscEventKind.Other;
    }
}
