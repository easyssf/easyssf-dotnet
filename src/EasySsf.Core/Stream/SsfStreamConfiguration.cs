using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Stream;

/// <summary>
/// The configuration of an event stream (SSF 1.0, section 8.1.1), as sent to and returned by the
/// transmitter.
/// </summary>
public sealed record SsfStreamConfiguration
{
    /// <param name="claims">the members of the stream configuration, an empty object if <see langword="null"/></param>
    public SsfStreamConfiguration(JsonElement? claims)
    {
        if (claims is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException("claims must be a JSON object", nameof(claims));
        }
        Claims = (claims is { ValueKind: JsonValueKind.Object } value) ? value.Clone() : SsfJson.EmptyObject;
    }

    /// <summary>
    /// The members of the stream configuration, a JSON object.
    /// </summary>
    public JsonElement Claims { get; }

    /// <param name="claims">the members of the stream configuration, may be <see langword="null"/></param>
    public static SsfStreamConfiguration From(JsonNode? claims)
    {
        return new SsfStreamConfiguration((claims is JsonObject) ? SsfJson.ToElement(claims) : null);
    }

    /// <summary>
    /// A stream the transmitter pushes events to.
    /// </summary>
    /// <param name="endpointUrl">the push endpoint of the receiver, as the transmitter reaches it</param>
    /// <param name="authorizationHeader">the <c>Authorization</c> header the transmitter has to send, may be <see langword="null"/></param>
    /// <param name="eventsRequested">the requested event types (alias or URI)</param>
    /// <param name="description">describes the stream, may be <see langword="null"/></param>
    public static SsfStreamConfiguration Push(Uri endpointUrl, string? authorizationHeader, IEnumerable<string> eventsRequested,
            string? description)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        JsonObject delivery = new()
        {
            ["method"] = SsfDeliveryMethod.Push.Uri(),
            ["endpoint_url"] = endpointUrl.ToString(),
        };
        if (authorizationHeader is not null)
        {
            delivery["authorization_header"] = authorizationHeader;
        }
        return Create(delivery, eventsRequested, description);
    }

    /// <summary>
    /// A stream the receiver polls events from.
    /// </summary>
    /// <param name="eventsRequested">the requested event types (alias or URI)</param>
    /// <param name="description">describes the stream, may be <see langword="null"/></param>
    public static SsfStreamConfiguration Poll(IEnumerable<string> eventsRequested, string? description)
    {
        return Create(new JsonObject { ["method"] = SsfDeliveryMethod.Poll.Uri() }, eventsRequested, description);
    }

    private static SsfStreamConfiguration Create(JsonObject delivery, IEnumerable<string> eventsRequested, string? description)
    {
        ArgumentNullException.ThrowIfNull(eventsRequested);
        JsonArray events = [];
        foreach (string eventType in eventsRequested)
        {
            events.Add((JsonNode)SsfEventTypes.Resolve(eventType));
        }
        JsonObject claims = new() { ["delivery"] = delivery, ["events_requested"] = events };
        if (description is not null)
        {
            claims["description"] = description;
        }
        return From(claims);
    }

    /// <summary>
    /// The claims as a mutable node, for a request to the transmitter.
    /// </summary>
    public JsonObject ToJsonObject()
    {
        return JsonObject.Create(Claims)!;
    }

    public string? StreamId => Claims.StringMember("stream_id");

    public string? Issuer => Claims.StringMember("iss");

    /// <summary>
    /// The audiences of the SETs of this stream, empty if unknown.
    /// </summary>
    public IReadOnlyList<string> Audience
    {
        get
        {
            JsonElement? audience = Claims.Member("aud");
            return (audience is { ValueKind: JsonValueKind.String } single) ? [single.GetString()!] : Claims.StringArrayMember("aud");
        }
    }

    public IReadOnlyList<string> EventsSupported => Claims.StringArrayMember("events_supported");

    public IReadOnlyList<string> EventsRequested => Claims.StringArrayMember("events_requested");

    public IReadOnlyList<string> EventsDelivered => Claims.StringArrayMember("events_delivered");

    public string? Description => Claims.StringMember("description");

    /// <summary>
    /// The <c>delivery</c> member, <see langword="null"/> if absent or not an object.
    /// </summary>
    public JsonElement? Delivery => (Claims.Member("delivery") is { ValueKind: JsonValueKind.Object } delivery) ? delivery : null;

    /// <summary>
    /// The identifier of the delivery method, see <see cref="SsfDeliveryMethods.Uri"/>.
    /// </summary>
    public string? DeliveryMethod => Delivery?.StringMember("method");

    /// <summary>
    /// The push endpoint of the receiver or the poll endpoint of the transmitter.
    /// </summary>
    /// <exception cref="UriFormatException">if the member is not a URI</exception>
    public Uri? DeliveryEndpointUrl
    {
        get
        {
            string? value = Delivery?.StringMember("endpoint_url");
            return (value is not null) ? new Uri(value, UriKind.RelativeOrAbsolute) : null;
        }
    }

    public bool Equals(SsfStreamConfiguration? other)
    {
        return other is not null && JsonElement.DeepEquals(Claims, other.Claims);
    }

    public override int GetHashCode()
    {
        return StreamId?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }

    public override string ToString()
    {
        return Claims.GetRawText();
    }
}
