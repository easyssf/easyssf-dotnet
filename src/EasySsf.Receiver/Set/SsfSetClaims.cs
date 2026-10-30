using System.Buffers.Text;
using System.Text.Json;

using EasySsf.Core.Support;

namespace EasySsf.Receiver.Set;

/// <summary>
/// Reads claims of a SET before it is verified, to decide how to verify it. Nothing read this way
/// is trusted.
/// </summary>
public static class SsfSetClaims
{
    /// <param name="encodedSet">the SET as received</param>
    /// <returns>
    /// the <c>iss</c> claim, <see langword="null"/> if the SET cannot be parsed or has none
    /// </returns>
    public static string? UnverifiedIssuer(string? encodedSet)
    {
        return UnverifiedPayload(encodedSet)?.StringMember("iss");
    }

    /// <param name="encodedSet">the SET as received</param>
    /// <returns>
    /// the <c>jti</c> claim, <see langword="null"/> if the SET cannot be parsed or has none
    /// </returns>
    public static string? UnverifiedJti(string? encodedSet)
    {
        return UnverifiedPayload(encodedSet)?.StringMember("jti");
    }

    /// <param name="encodedSet">the SET as received</param>
    /// <returns>
    /// the claims of the SET, <see langword="null"/> if the SET is not a JWS in compact
    /// serialization whose payload is a JSON object
    /// </returns>
    public static JsonElement? UnverifiedPayload(string? encodedSet)
    {
        if (string.IsNullOrWhiteSpace(encodedSet))
        {
            return null;
        }
        string[] parts = encodedSet.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        try
        {
            JsonElement payload = SsfJson.Parse(Base64Url.DecodeFromChars(parts[1]));
            return (payload.ValueKind == JsonValueKind.Object) ? payload : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
