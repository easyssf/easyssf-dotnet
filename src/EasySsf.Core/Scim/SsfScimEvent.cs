using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Scim;

/// <summary>
/// A SCIM Event (RFC 9967) of a SET, with typed access to the common payload attributes of section
/// 2.2: the resource as <c>data</c> in <c>full</c> mode, the modified <c>attributes</c> in
/// <c>notice</c> mode, and the ETag <c>version</c> of the resource after the event. The <c>txn</c>
/// that groups the SETs of one transaction is a claim of the <see cref="SsfEventToken"/>.
/// <para>
/// A SET may carry several SCIM Events; RFC 9967 then has them arise from the same change of the
/// same resource, so they share the <see cref="Subject"/>.
/// </para>
/// </summary>
public sealed record SsfScimEvent
{
    /// <param name="eventType">the event type URI</param>
    /// <param name="operation">what the event reports</param>
    /// <param name="subject">the SCIM resource the event is about, <see langword="null"/> if the <c>sub_id</c> of the SET is not a <c>scim</c> subject identifier (which RFC 9967 requires)</param>
    /// <param name="payload">the event payload as received, an empty object if <see langword="null"/> or not an object</param>
    public SsfScimEvent(string eventType, SsfScimOperation operation, SsfScimSubject? subject, JsonElement? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        EventType = eventType;
        Operation = operation;
        Subject = subject;
        Payload = (payload is { ValueKind: JsonValueKind.Object } value) ? value.Clone() : SsfJson.EmptyObject;
    }

    /// <summary>
    /// The event type URI.
    /// </summary>
    public string EventType { get; }

    /// <summary>
    /// What the event reports.
    /// </summary>
    public SsfScimOperation Operation { get; }

    /// <summary>
    /// The SCIM resource the event is about, <see langword="null"/> if the <c>sub_id</c> of the SET
    /// is not a <c>scim</c> subject identifier (which RFC 9967 requires).
    /// </summary>
    public SsfScimSubject? Subject { get; }

    /// <summary>
    /// The event payload as received, a JSON object.
    /// </summary>
    public JsonElement Payload { get; }

    /// <param name="eventType">the event type URI or alias</param>
    /// <param name="subject">the SCIM subject of the SET, may be <see langword="null"/></param>
    /// <param name="payload">the event payload, may be <see langword="null"/></param>
    /// <returns>the event, <see langword="null"/> if the event type is not one of SCIM Events</returns>
    public static SsfScimEvent? Of(string eventType, SsfScimSubject? subject, JsonElement? payload)
    {
        SsfScimOperation? operation = SsfScimOperations.Of(eventType);
        return (operation is null) ? null : new SsfScimEvent(SsfEventTypes.Resolve(eventType), operation.Value, subject, payload);
    }

    /// <summary>
    /// The SCIM Events of a SET, in the order of its <c>events</c> claim.
    /// </summary>
    /// <param name="eventToken">a verified SET</param>
    /// <returns>the SCIM Events, empty if the SET carries none</returns>
    public static IReadOnlyList<SsfScimEvent> In(SsfEventToken eventToken)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        SsfScimSubject? subject = SsfScimSubject.From(SsfSubject.From(eventToken.SubjectId));
        List<SsfScimEvent> events = [];
        foreach ((string eventType, JsonElement payload) in eventToken.Events)
        {
            if (SsfScimOperations.Of(eventType) is { } operation)
            {
                events.Add(new SsfScimEvent(eventType, operation, subject, payload));
            }
        }
        return events.AsReadOnly();
    }

    /// <summary>
    /// Whether the payload carries the resource as <see cref="Data"/>: the event type ends in
    /// <c>:full</c>.
    /// </summary>
    public bool IsFull => EventType.EndsWith(":full", StringComparison.Ordinal);

    /// <summary>
    /// Whether the payload only names the modified <see cref="Attributes"/>: the event type ends in
    /// <c>:notice</c>. A receiver may fetch the resource from the SCIM service provider with a GET
    /// of the subject's uri.
    /// </summary>
    public bool IsNotice => EventType.EndsWith(":notice", StringComparison.Ordinal);

    /// <summary>
    /// The <c>data</c> of a <c>full</c> event: the representation of the created or replaced
    /// resource, or the <c>PatchOp</c> applied; <see langword="null"/> if absent.
    /// </summary>
    public JsonElement? Data => ObjectMember("data");

    /// <summary>
    /// The <c>attributes</c> of a <c>notice</c> event: the paths of the attributes created,
    /// modified or removed; empty if absent.
    /// </summary>
    public IReadOnlyList<string> Attributes => Payload.StringArrayMember("attributes");

    /// <summary>
    /// The ETag <c>version</c> of the resource after the event, <see langword="null"/> if absent.
    /// </summary>
    public string? Version => Payload.TextMember("version");

    /// <summary>
    /// The HTTP <c>method</c> of the request an <see cref="SsfScimOperation.AsyncResponse"/>
    /// reports on, <see langword="null"/> for other events.
    /// </summary>
    public string? Method => Payload.TextMember("method");

    /// <summary>
    /// The HTTP <c>status</c> of the request an <see cref="SsfScimOperation.AsyncResponse"/>
    /// reports on, as a string like the SCIM bulk response has it; <see langword="null"/> for other
    /// events.
    /// </summary>
    public string? Status => Payload.TextMember("status");

    /// <summary>
    /// The SCIM error <c>response</c> of a failed request an
    /// <see cref="SsfScimOperation.AsyncResponse"/> reports on, with <c>scimType</c> and
    /// <c>detail</c>; <see langword="null"/> if the request succeeded or for other events.
    /// </summary>
    public JsonElement? Response => ObjectMember("response");

    private JsonElement? ObjectMember(string name)
    {
        return (Payload.Member(name) is { ValueKind: JsonValueKind.Object } member) ? member : null;
    }

    public bool Equals(SsfScimEvent? other)
    {
        return other is not null && EventType == other.EventType && Operation == other.Operation
            && Equals(Subject, other.Subject) && JsonElement.DeepEquals(Payload, other.Payload);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(EventType, Operation, Subject);
    }

    public override string ToString()
    {
        return SsfEventTypes.AliasOf(EventType) + " " + (Subject?.Uri ?? "(no scim subject)") + " " + Payload.GetRawText();
    }
}
