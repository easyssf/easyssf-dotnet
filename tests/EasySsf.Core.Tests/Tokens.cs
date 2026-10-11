using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Tests;

/// <summary>
/// Builds verified-looking SETs for the tests of the typed events.
/// </summary>
public static class Tokens
{
    public static SsfEventToken Of(JsonObject events, JsonObject? subjectId, string? txn = null,
            string issuer = "https://idp.example.com")
    {
        JsonElement eventsElement = SsfJson.ToElement(events);
        List<KeyValuePair<string, JsonElement>> payloads = eventsElement.EnumerateObject()
            .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value))
            .ToList();
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventToken("jti-1", issuer, DateTimeOffset.Parse("2026-10-06T10:00:00Z"), null, payloads, subject,
            txn, SsfJson.EmptyObject);
    }

    public static JsonObject Events(string eventType, JsonNode? payload)
    {
        return new JsonObject { [eventType] = payload?.DeepClone() };
    }
}
