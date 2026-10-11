using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;

namespace EasySsf.Receiver.Tests;

/// <summary>
/// Builds event contexts from JSON, for the tests of the typed handlers.
/// </summary>
public static class Contexts
{
    public static SsfEventContext Of(JsonObject events, JsonObject? subjectId, string issuer = "https://idp.example.com")
    {
        JsonElement eventsElement = SsfJson.ToElement(events);
        List<KeyValuePair<string, JsonElement>> payloads = eventsElement.EnumerateObject()
            .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value))
            .ToList();
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventContext(new SsfEventToken("jti-1", issuer, DateTimeOffset.Parse("2026-10-06T10:00:00Z"), null,
            payloads, subject, null, SsfJson.EmptyObject));
    }

    public static JsonObject Events(string eventType, JsonNode? payload)
    {
        return new JsonObject { [eventType] = payload?.DeepClone() };
    }
}
