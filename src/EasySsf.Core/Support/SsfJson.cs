using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasySsf.Core.Support;

/// <summary>
/// JSON helpers for the value objects of the protocol, which hold their raw claims as
/// <see cref="JsonElement"/>s: immutable, and cloned so that they outlive the document they
/// were parsed from.
/// </summary>
public static class SsfJson
{
    /// <summary>
    /// An empty JSON object.
    /// </summary>
    public static JsonElement EmptyObject { get; } = Parse("{}");

    /// <summary>
    /// Parses a JSON document into an element that does not depend on the document.
    /// </summary>
    /// <exception cref="JsonException">if the text is not valid JSON</exception>
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Parses a JSON document into an element that does not depend on the document.
    /// </summary>
    /// <exception cref="JsonException">if the text is not valid JSON</exception>
    public static JsonElement Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Converts a node, for example a <see cref="JsonObject"/> built in code, to an element.
    /// </summary>
    /// <returns>the element, a JSON <c>null</c> for a <see langword="null"/> node</returns>
    public static JsonElement ToElement(JsonNode? node)
    {
        if (node is null)
        {
            return Parse("null");
        }
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            node.WriteTo(writer);
        }
        return Parse(buffer.WrittenMemory);
    }

    /// <summary>
    /// Whether the element is a JSON object without members.
    /// </summary>
    public static bool IsEmptyObject(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        using JsonElement.ObjectEnumerator members = element.EnumerateObject();
        return !members.MoveNext();
    }

    /// <summary>
    /// Whether the element is a JSON object with at least one member.
    /// </summary>
    public static bool IsNonEmptyObject(this JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object && !element.IsEmptyObject();
    }

    /// <summary>
    /// Reads a member of a JSON object.
    /// </summary>
    /// <returns>
    /// the member, <see langword="null"/> if the element is not an object or has no such member
    /// </returns>
    public static JsonElement? Member(this JsonElement element, string name)
    {
        return (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement member))
            ? member : null;
    }

    /// <summary>
    /// Reads a string member of a JSON object.
    /// </summary>
    /// <returns>
    /// the value, <see langword="null"/> if the element is not an object, has no such member or
    /// the member is not a non-empty string
    /// </returns>
    public static string? StringMember(this JsonElement element, string name)
    {
        JsonElement? member = element.Member(name);
        if (member is not { ValueKind: JsonValueKind.String })
        {
            return null;
        }
        string? value = member.Value.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
