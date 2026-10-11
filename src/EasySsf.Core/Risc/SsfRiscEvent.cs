using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Risc;

/// <summary>
/// A RISC 1.0 event of a SET, with typed access to the few attributes RISC defines: the
/// <see cref="Reason"/> of an account-disabled, the <see cref="NewValue"/> of an
/// identifier-changed, and the <see cref="CredentialType"/>, <see cref="EventTimestamp"/> and
/// reasons of a credential-compromise. Most RISC events have no attributes; the
/// <see cref="Payload"/> is the event as received for anything else. An accessor returns
/// <see langword="null"/> when the attribute is absent, whether because the transmitter left it
/// out or because the <see cref="Kind"/> does not define it.
/// <para>
/// The subject of the event is the <c>sub_id</c> of the SET, <c>SsfEventContext.Subject</c> in a
/// handler.
/// </para>
/// </summary>
public sealed record SsfRiscEvent
{
    /// <param name="eventType">the event type URI</param>
    /// <param name="kind">the kind of event, <see cref="SsfRiscEventKind.Other"/> for a RISC event type this version does not know</param>
    /// <param name="payload">the event payload as received, an empty object if <see langword="null"/> or not an object</param>
    public SsfRiscEvent(string eventType, SsfRiscEventKind kind, JsonElement? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        EventType = eventType;
        Kind = kind;
        Payload = (payload is { ValueKind: JsonValueKind.Object } value) ? value.Clone() : SsfJson.EmptyObject;
    }

    /// <summary>
    /// The event type URI.
    /// </summary>
    public string EventType { get; }

    /// <summary>
    /// The kind of event, <see cref="SsfRiscEventKind.Other"/> for a RISC event type this version
    /// does not know.
    /// </summary>
    public SsfRiscEventKind Kind { get; }

    /// <summary>
    /// The event payload as received, a JSON object.
    /// </summary>
    public JsonElement Payload { get; }

    /// <param name="eventType">the event type URI or alias</param>
    /// <param name="payload">the event payload, may be <see langword="null"/></param>
    /// <returns>the event, <see langword="null"/> if the event type is not a RISC event</returns>
    public static SsfRiscEvent? Of(string eventType, JsonElement? payload)
    {
        SsfRiscEventKind? kind = SsfRiscEventKinds.Of(eventType);
        return (kind is null) ? null : new SsfRiscEvent(SsfEventTypes.Resolve(eventType), kind.Value, payload);
    }

    /// <summary>
    /// The RISC events of a SET, in the order of its <c>events</c> claim.
    /// </summary>
    /// <param name="eventToken">a verified SET</param>
    /// <returns>the RISC events, empty if the SET carries none</returns>
    public static IReadOnlyList<SsfRiscEvent> In(SsfEventToken eventToken)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        List<SsfRiscEvent> events = [];
        foreach ((string eventType, JsonElement payload) in eventToken.Events)
        {
            if (SsfRiscEventKinds.Of(eventType) is { } kind)
            {
                events.Add(new SsfRiscEvent(eventType, kind, payload));
            }
        }
        return events.AsReadOnly();
    }

    /// <summary>
    /// The <c>reason</c> of an <see cref="SsfRiscEventKind.AccountDisabled"/>: <c>hijacking</c> or
    /// <c>bulk-account</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? Reason => Payload.TextMember("reason");

    /// <summary>
    /// The <c>new-value</c> of an <see cref="SsfRiscEventKind.IdentifierChanged"/>, the new
    /// identifier; <see langword="null"/> if absent.
    /// </summary>
    public string? NewValue => Payload.TextMember("new-value");

    /// <summary>
    /// The <c>credential_type</c> of a <see cref="SsfRiscEventKind.CredentialCompromise"/>, one of
    /// the values of CAEP's credential-change (<c>password</c>, <c>pin</c>, <c>x509</c>, ...);
    /// <see langword="null"/> if absent.
    /// </summary>
    public string? CredentialType => Payload.TextMember("credential_type");

    /// <summary>
    /// The <c>event_timestamp</c> of a <see cref="SsfRiscEventKind.CredentialCompromise"/>, when
    /// the transmitter discovered the compromise, <see langword="null"/> if absent. RISC defines
    /// seconds since the epoch; a value that is clearly milliseconds is accepted too.
    /// </summary>
    public DateTimeOffset? EventTimestamp => SsfEventTimestamps.From(Payload.Member("event_timestamp"));

    /// <summary>
    /// The <c>reason_admin</c> of a <see cref="SsfRiscEventKind.CredentialCompromise"/>, the reason
    /// for administrators, <see langword="null"/> if absent. RISC leaves the type open; a
    /// localizable object as CAEP defines it is returned as its JSON text.
    /// </summary>
    public string? ReasonAdmin => Payload.TextMember("reason_admin");

    /// <summary>
    /// The <c>reason_user</c> of a <see cref="SsfRiscEventKind.CredentialCompromise"/>, the reason
    /// for the end user, <see langword="null"/> if absent.
    /// </summary>
    public string? ReasonUser => Payload.TextMember("reason_user");

    public bool Equals(SsfRiscEvent? other)
    {
        return other is not null && EventType == other.EventType && Kind == other.Kind
            && JsonElement.DeepEquals(Payload, other.Payload);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(EventType, Kind);
    }

    public override string ToString()
    {
        return SsfEventTypes.AliasOf(EventType) + " " + Payload.GetRawText();
    }
}
