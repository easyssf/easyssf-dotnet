using System.Collections.ObjectModel;
using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Caep;

/// <summary>
/// A CAEP 1.0 event of a SET, with typed access to the claims every CAEP event may carry (section
/// 2: <see cref="EventTimestamp"/>, <see cref="InitiatingEntity"/>, <see cref="ReasonAdmin"/>,
/// <see cref="ReasonUser"/>) and to the event-specific claims of section 3. An accessor returns
/// <see langword="null"/> (or an empty collection) when the claim is absent, whether because the
/// transmitter left it out or because the <see cref="Kind"/> does not define it. The
/// <see cref="Payload"/> is the event as received, for anything else.
/// <para>
/// The subject of the event is the <c>sub_id</c> of the SET, <c>SsfEventContext.Subject</c> in a
/// handler: CAEP does not prescribe a subject format.
/// </para>
/// </summary>
public sealed record SsfCaepEvent
{
    /// <param name="eventType">the event type URI</param>
    /// <param name="kind">the kind of event, <see cref="SsfCaepEventKind.Other"/> for a CAEP event type this version does not know</param>
    /// <param name="payload">the event payload as received, an empty object if <see langword="null"/> or not an object</param>
    public SsfCaepEvent(string eventType, SsfCaepEventKind kind, JsonElement? payload)
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
    /// The kind of event, <see cref="SsfCaepEventKind.Other"/> for a CAEP event type this version
    /// does not know.
    /// </summary>
    public SsfCaepEventKind Kind { get; }

    /// <summary>
    /// The event payload as received, a JSON object.
    /// </summary>
    public JsonElement Payload { get; }

    /// <param name="eventType">the event type URI or alias</param>
    /// <param name="payload">the event payload, may be <see langword="null"/></param>
    /// <returns>the event, <see langword="null"/> if the event type is not a CAEP event</returns>
    public static SsfCaepEvent? Of(string eventType, JsonElement? payload)
    {
        SsfCaepEventKind? kind = SsfCaepEventKinds.Of(eventType);
        return (kind is null) ? null : new SsfCaepEvent(SsfEventTypes.Resolve(eventType), kind.Value, payload);
    }

    /// <summary>
    /// The CAEP events of a SET, in the order of its <c>events</c> claim. CAEP allows one event
    /// per SET; a SET with several is read like one of each.
    /// </summary>
    /// <param name="eventToken">a verified SET</param>
    /// <returns>the CAEP events, empty if the SET carries none</returns>
    public static IReadOnlyList<SsfCaepEvent> In(SsfEventToken eventToken)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        List<SsfCaepEvent> events = [];
        foreach ((string eventType, JsonElement payload) in eventToken.Events)
        {
            if (SsfCaepEventKinds.Of(eventType) is { } kind)
            {
                events.Add(new SsfCaepEvent(eventType, kind, payload));
            }
        }
        return events.AsReadOnly();
    }

    // CAEP section 2: claims every event may carry

    /// <summary>
    /// The <c>event_timestamp</c>, the time the event occurred, <see langword="null"/> if absent.
    /// CAEP defines seconds since the epoch; a value that is clearly milliseconds is accepted too.
    /// </summary>
    public DateTimeOffset? EventTimestamp => SsfEventTimestamps.From(Payload.Member("event_timestamp"));

    /// <summary>
    /// The <c>initiating_entity</c>, who invoked the event: <c>admin</c>, <c>user</c>,
    /// <c>policy</c> or <c>system</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? InitiatingEntity => Text("initiating_entity");

    /// <summary>
    /// The <c>reason_admin</c>, the reason for administrators, by BCP 47 language tag (<c>en</c>,
    /// <c>de-CH</c>, ...); empty if absent. A transmitter that sends a plain string instead of the
    /// localizable object of CAEP gets the key <c>""</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReasonAdmin => Localized("reason_admin");

    /// <summary>
    /// The <c>reason_user</c>, the reason for display to the end user, by BCP 47 language tag;
    /// empty if absent, a plain string under the key <c>""</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReasonUser => Localized("reason_user");

    // CAEP section 3: event-specific claims

    /// <summary>
    /// The <c>claims</c> of a <see cref="SsfCaepEventKind.TokenClaimsChange"/>, each with its new
    /// value; <see langword="null"/> if absent.
    /// </summary>
    public JsonElement? Claims => ObjectMember("claims");

    /// <summary>
    /// The <c>credential_type</c> of a <see cref="SsfCaepEventKind.CredentialChange"/>:
    /// <c>password</c>, <c>pin</c>, <c>x509</c>, <c>fido2-platform</c>, <c>fido2-roaming</c>,
    /// <c>fido-u2f</c>, <c>verifiable-credential</c>, <c>phone-voice</c>, <c>phone-sms</c>,
    /// <c>app</c> or a value agreed with the transmitter; <see langword="null"/> if absent.
    /// </summary>
    public string? CredentialType => Text("credential_type");

    /// <summary>
    /// The <c>change_type</c> of a <see cref="SsfCaepEventKind.CredentialChange"/>: <c>create</c>,
    /// <c>revoke</c>, <c>update</c> or <c>delete</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? ChangeType => Text("change_type");

    /// <summary>
    /// The <c>friendly_name</c> of the credential of a
    /// <see cref="SsfCaepEventKind.CredentialChange"/>, <see langword="null"/> if absent.
    /// </summary>
    public string? FriendlyName => Text("friendly_name");

    /// <summary>
    /// The <c>x509_issuer</c> of an X.509 credential, <see langword="null"/> if absent.
    /// </summary>
    public string? X509Issuer => Text("x509_issuer");

    /// <summary>
    /// The <c>x509_serial</c> of an X.509 credential, <see langword="null"/> if absent.
    /// </summary>
    public string? X509Serial => Text("x509_serial");

    /// <summary>
    /// The <c>fido2_aaguid</c>, the FIDO2 Authenticator Attestation GUID of the credential,
    /// <see langword="null"/> if absent.
    /// </summary>
    public string? Fido2Aaguid => Text("fido2_aaguid");

    /// <summary>
    /// The <c>namespace</c> of the levels of an <see cref="SsfCaepEventKind.AssuranceLevelChange"/>:
    /// <c>RFC8176</c>, <c>RFC6711</c>, <c>ISO-IEC-29115</c>, <c>NIST-IAL</c>, <c>NIST-AAL</c>,
    /// <c>NIST-FAL</c> or a value agreed with the transmitter; <see langword="null"/> if absent.
    /// </summary>
    public string? Namespace => Text("namespace");

    /// <summary>
    /// The <c>current_level</c>: the assurance level of an
    /// <see cref="SsfCaepEventKind.AssuranceLevelChange"/> in its <see cref="Namespace"/>, or the
    /// risk level of a <see cref="SsfCaepEventKind.RiskLevelChange"/> (<c>LOW</c>, <c>MEDIUM</c>,
    /// <c>HIGH</c>); <see langword="null"/> if absent.
    /// </summary>
    public string? CurrentLevel => Text("current_level");

    /// <summary>
    /// The <c>previous_level</c> of an assurance or risk level change, <see langword="null"/> if
    /// absent: the transmitter does not know the previous level.
    /// </summary>
    public string? PreviousLevel => Text("previous_level");

    /// <summary>
    /// The <c>change_direction</c> of an <see cref="SsfCaepEventKind.AssuranceLevelChange"/>:
    /// <c>increase</c> or <c>decrease</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? ChangeDirection => Text("change_direction");

    /// <summary>
    /// The <c>current_status</c> of a <see cref="SsfCaepEventKind.DeviceComplianceChange"/>:
    /// <c>compliant</c> or <c>not-compliant</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? CurrentStatus => Text("current_status");

    /// <summary>
    /// The <c>previous_status</c> of a <see cref="SsfCaepEventKind.DeviceComplianceChange"/>:
    /// <c>compliant</c> or <c>not-compliant</c>; <see langword="null"/> if absent.
    /// </summary>
    public string? PreviousStatus => Text("previous_status");

    /// <summary>
    /// The <c>fp_ua</c>, the fingerprint of the user agent of a
    /// <see cref="SsfCaepEventKind.SessionEstablished"/> or
    /// <see cref="SsfCaepEventKind.SessionPresented"/>; <see langword="null"/> if absent.
    /// </summary>
    public string? FpUa => Text("fp_ua");

    /// <summary>
    /// The <c>acr</c>, the authentication context class reference of a
    /// <see cref="SsfCaepEventKind.SessionEstablished"/>, as in an ID Token; <see langword="null"/>
    /// if absent.
    /// </summary>
    public string? Acr => Text("acr");

    /// <summary>
    /// The <c>amr</c>, the authentication methods references of a
    /// <see cref="SsfCaepEventKind.SessionEstablished"/>, as in an ID Token; empty if absent.
    /// </summary>
    public IReadOnlyList<string> Amr => Payload.StringArrayMember("amr");

    /// <summary>
    /// The <c>ext_id</c>, the external session identifier of a
    /// <see cref="SsfCaepEventKind.SessionEstablished"/> or
    /// <see cref="SsfCaepEventKind.SessionPresented"/> that correlates it with a broader session,
    /// for example a SAML one; <see langword="null"/> if absent.
    /// </summary>
    public string? ExtId => Text("ext_id");

    /// <summary>
    /// The <c>principal</c> of a <see cref="SsfCaepEventKind.RiskLevelChange"/>, the entity whose
    /// risk changed: <c>USER</c>, <c>DEVICE</c>, <c>SESSION</c>, <c>TENANT</c>, <c>ORG_UNIT</c>,
    /// <c>GROUP</c> or another entity of SSF; <see langword="null"/> if absent.
    /// </summary>
    public string? Principal => Text("principal");

    /// <summary>
    /// The <c>risk_reason</c> of a <see cref="SsfCaepEventKind.RiskLevelChange"/>,
    /// <see langword="null"/> if absent.
    /// </summary>
    public string? RiskReason => Text("risk_reason");

    private string? Text(string name)
    {
        return Payload.TextMember(name);
    }

    private JsonElement? ObjectMember(string name)
    {
        return (Payload.Member(name) is { ValueKind: JsonValueKind.Object } member) ? member : null;
    }

    private IReadOnlyDictionary<string, string> Localized(string name)
    {
        JsonElement? value = Payload.Member(name);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        }
        Dictionary<string, string> reasons = new(StringComparer.Ordinal);
        if (value.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty byLanguage in value.Value.EnumerateObject())
            {
                string? text = value.Value.TextMember(byLanguage.Name);
                if (text is not null)
                {
                    reasons[byLanguage.Name] = text;
                }
            }
        }
        else
        {
            reasons[""] = Payload.TextMember(name)!;
        }
        return new ReadOnlyDictionary<string, string>(reasons);
    }

    public bool Equals(SsfCaepEvent? other)
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
