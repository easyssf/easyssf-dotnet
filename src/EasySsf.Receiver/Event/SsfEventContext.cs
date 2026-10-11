using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Receiver.Event;

/// <summary>
/// A verified <see cref="SsfEventToken"/> together with convenient, alias-aware access to its
/// events and subjects. Passed to every <see cref="ISsfEventHandler"/>.
/// </summary>
public sealed class SsfEventContext
{
    private SsfSubject? _subject;

    private IReadOnlyCollection<string>? _eventTypes;

    public SsfEventContext(SsfEventToken eventToken)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        EventToken = eventToken;
    }

    /// <summary>
    /// The verified SET.
    /// </summary>
    public SsfEventToken EventToken { get; }

    /// <summary>
    /// The key for making the side effects of a handler idempotent, the issuer and <c>jti</c> of
    /// the SET: a SET is handled at least once, not exactly once (see
    /// <see cref="ISsfEventHandler"/>).
    /// </summary>
    public string IdempotencyKey => EventToken.IdempotencyKey;

    /// <summary>
    /// The event type URIs contained in the SET.
    /// </summary>
    public IReadOnlyCollection<string> EventTypes => _eventTypes ??= EventToken.Events.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Whether the SET contains an event of the given type.
    /// </summary>
    /// <param name="aliasOrUri">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <seealso cref="SsfEventTypes"/>
    public bool HasEvent(string? aliasOrUri)
    {
        return aliasOrUri is not null && EventToken.Events.ContainsKey(SsfEventTypes.Resolve(aliasOrUri));
    }

    /// <summary>
    /// The payload of the event of the given type.
    /// </summary>
    /// <param name="aliasOrUri">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <returns>
    /// the payload, or <see langword="null"/> if the SET has no such event or its payload is not a
    /// JSON object
    /// </returns>
    public JsonElement? EventFor(string? aliasOrUri)
    {
        if (aliasOrUri is null || !EventToken.Events.TryGetValue(SsfEventTypes.Resolve(aliasOrUri), out JsonElement payload))
        {
            return null;
        }
        return (payload.ValueKind == JsonValueKind.Object) ? payload : null;
    }

    /// <summary>
    /// The subject of the SET, taken from its top-level <c>sub_id</c> claim;
    /// <see cref="SsfSubject.IsEmpty">empty</see> if the SET has none.
    /// </summary>
    public SsfSubject Subject => _subject ??= SsfSubject.From(EventToken.SubjectId);

    /// <summary>
    /// The subject of the event of the given type: the top-level <c>sub_id</c> claim of the SET
    /// or, for transmitters following earlier SSF drafts, the <c>subject</c> member of the event
    /// payload. SETs without the top-level claim reach a handler only in the
    /// <see cref="SubjectCompatibilityMode.Legacy"/> subject compatibility mode of the verifier.
    /// </summary>
    public SsfSubject SubjectFor(string? aliasOrUri)
    {
        SsfSubject subject = Subject;
        if (!subject.IsEmpty)
        {
            return subject;
        }
        JsonElement? legacySubject = EventFor(aliasOrUri)?.Member("subject");
        return (legacySubject is { ValueKind: JsonValueKind.Object }) ? SsfSubject.From(legacySubject) : subject;
    }

    /// <summary>
    /// The time the event of the given type occurred: its <c>event_timestamp</c> or, if absent,
    /// the time the SET was issued.
    /// </summary>
    public DateTimeOffset EventTimestamp(string? aliasOrUri)
    {
        return SsfEventTimestamps.From(EventFor(aliasOrUri)?.Member("event_timestamp")) ?? EventToken.Iat;
    }
}
