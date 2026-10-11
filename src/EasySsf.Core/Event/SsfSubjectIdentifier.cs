using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Support;

namespace EasySsf.Core.Event;

/// <summary>
/// A subject identifier (RFC 9493): a <c>format</c> and the members that format defines. Any
/// format is represented, the ones of RFC 9493 and the <c>scim</c> format of SCIM Events (RFC
/// 9967) are known by name.
/// </summary>
public sealed record SsfSubjectIdentifier
{
    public const string Account = "account";

    public const string Email = "email";

    public const string IssSub = "iss_sub";

    public const string Opaque = "opaque";

    public const string PhoneNumber = "phone_number";

    public const string Did = "did";

    public const string Uri = "uri";

    public const string Aliases = "aliases";

    /// <summary>
    /// The format of SCIM Events (RFC 9967, section 2.1): a SCIM resource named by its relative
    /// <c>uri</c>, with its <c>externalId</c> and <c>id</c> if known.
    /// </summary>
    public const string Scim = "scim";

    /// <param name="format">the format, never empty</param>
    /// <param name="claims">the members of the identifier as received, including <c>format</c></param>
    /// <exception cref="ArgumentException">
    /// if the format is empty or the claims are not a JSON object
    /// </exception>
    public SsfSubjectIdentifier(string format, JsonElement claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (claims.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("claims must be a JSON object", nameof(claims));
        }
        Format = format;
        Claims = claims.Clone();
    }

    /// <summary>
    /// The format, never empty.
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// The members of the identifier as received, including <c>format</c>: a JSON object.
    /// </summary>
    public JsonElement Claims { get; }

    /// <param name="identifier">the identifier as received, with a <c>format</c> member</param>
    /// <returns>
    /// the identifier, <see langword="null"/> if the element is not a JSON object or has no
    /// non-empty <c>format</c>
    /// </returns>
    public static SsfSubjectIdentifier? From(JsonElement? identifier)
    {
        if (identifier is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }
        string? format = element.StringMember("format");
        return (format is not null) ? new SsfSubjectIdentifier(format, element) : null;
    }

    /// <param name="identifier">the identifier as received, with a <c>format</c> member</param>
    /// <returns>
    /// the identifier, <see langword="null"/> if the node is not a JSON object or has no non-empty
    /// <c>format</c>
    /// </returns>
    public static SsfSubjectIdentifier? From(JsonNode? identifier)
    {
        return (identifier is JsonObject) ? From(SsfJson.ToElement(identifier)) : null;
    }

    public bool Is(string format)
    {
        return Format == format;
    }

    public bool IsIssSub => Is(IssSub);

    public bool IsEmail => Is(Email);

    public bool IsOpaque => Is(Opaque);

    public bool IsScim => Is(Scim);

    /// <returns>
    /// a member of the identifier, <see langword="null"/> if absent or not a non-empty string
    /// </returns>
    public string? Claim(string name)
    {
        return Claims.StringMember(name);
    }

    /// <summary>
    /// The issuer of an <c>iss_sub</c> identifier, <see langword="null"/> for other formats.
    /// </summary>
    public string? Issuer => IsIssSub ? Claim("iss") : null;

    /// <summary>
    /// The member that identifies the subject within its format: <c>sub</c> of <c>iss_sub</c>,
    /// <c>email</c>, <c>id</c> of <c>opaque</c>, <c>uri</c> of <c>account</c>, <c>uri</c> and
    /// <c>scim</c>, <c>phone_number</c>, <c>url</c> of <c>did</c>.
    /// </summary>
    /// <returns>the value, <see langword="null"/> for <c>aliases</c> and unknown formats</returns>
    public string? Value => Format switch
    {
        IssSub => Claim("sub"),
        Email => Claim("email"),
        Opaque => Claim("id"),
        Account or Uri or Scim => Claim("uri"),
        PhoneNumber => Claim("phone_number"),
        Did => Claim("url"),
        _ => null,
    };

    /// <summary>
    /// The identifiers of an <c>aliases</c> identifier, empty for other formats.
    /// </summary>
    public IReadOnlyList<SsfSubjectIdentifier> AliasIdentifiers()
    {
        if (!Is(Aliases) || Claims.Member("identifiers") is not { ValueKind: JsonValueKind.Array } identifiers)
        {
            return [];
        }
        List<SsfSubjectIdentifier> result = [];
        foreach (JsonElement identifier in identifiers.EnumerateArray())
        {
            if (From(identifier) is { } alias)
            {
                result.Add(alias);
            }
        }
        return result.AsReadOnly();
    }

    public bool Equals(SsfSubjectIdentifier? other)
    {
        return other is not null && Format == other.Format && JsonElement.DeepEquals(Claims, other.Claims);
    }

    public override int GetHashCode()
    {
        return Format.GetHashCode(StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return Claims.GetRawText();
    }
}
