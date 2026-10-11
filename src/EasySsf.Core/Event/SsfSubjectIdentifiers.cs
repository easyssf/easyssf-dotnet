using System.Text.Json.Nodes;

namespace EasySsf.Core.Event;

/// <summary>
/// Creates subject identifiers (RFC 9493) as they appear in the <c>sub_id</c> claim of a SET and
/// in requests to add or remove subjects of a stream. The identifiers are
/// <see cref="JsonObject"/>s, so that they can be composed and serialized; nodes passed in are
/// copied, as a node can only have one parent.
/// </summary>
/// <seealso cref="SsfSubject"/>
public static class SsfSubjectIdentifiers
{
    /// <summary>
    /// A subject identified by its issuer and its identifier at that issuer.
    /// </summary>
    public static JsonObject IssSub(string issuer, string subject)
    {
        return Identifier("iss_sub", ("iss", issuer), ("sub", subject));
    }

    public static JsonObject Email(string email)
    {
        return Identifier("email", ("email", email));
    }

    public static JsonObject Opaque(string id)
    {
        return Identifier("opaque", ("id", id));
    }

    /// <summary>
    /// A subject identified by an <c>acct:</c> URI (RFC 7565).
    /// </summary>
    public static JsonObject Account(string uri)
    {
        return Identifier("account", ("uri", uri));
    }

    /// <summary>
    /// A subject identified by a telephone number in E.164 format.
    /// </summary>
    public static JsonObject PhoneNumber(string phoneNumber)
    {
        return Identifier("phone_number", ("phone_number", phoneNumber));
    }

    /// <summary>
    /// A subject identified by a decentralized identifier.
    /// </summary>
    public static JsonObject Did(string url)
    {
        return Identifier("did", ("url", url));
    }

    /// <summary>
    /// A subject identified by a URI.
    /// </summary>
    public static JsonObject Uri(string uri)
    {
        return Identifier("uri", ("uri", uri));
    }

    /// <summary>
    /// A SCIM resource (RFC 9967), the subject of SCIM Events.
    /// </summary>
    /// <param name="uri">the relative path of the resource, for example <c>/Users/2b2f880a</c></param>
    public static JsonObject Scim(string uri)
    {
        return Identifier("scim", ("uri", uri));
    }

    /// <summary>
    /// A SCIM resource (RFC 9967), the subject of SCIM Events.
    /// </summary>
    /// <param name="uri">the relative path of the resource, for example <c>/Users/2b2f880a</c></param>
    /// <param name="externalId">the <c>externalId</c> of the resource, may be <see langword="null"/></param>
    public static JsonObject Scim(string uri, string? externalId)
    {
        JsonObject identifier = Scim(uri);
        if (externalId is not null)
        {
            identifier["externalId"] = externalId;
        }
        return identifier;
    }

    /// <summary>
    /// A subject with several identifiers that all name it.
    /// </summary>
    public static JsonObject Aliases(IEnumerable<JsonObject> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        JsonArray copies = [];
        foreach (JsonObject identifier in identifiers)
        {
            copies.Add(identifier.DeepClone());
        }
        return new JsonObject { ["format"] = "aliases", ["identifiers"] = copies };
    }

    /// <summary>
    /// A complex subject with the given members (<c>user</c>, <c>session</c>, <c>device</c>,
    /// <c>tenant</c>, ...), each a subject identifier; members that are <see langword="null"/>
    /// are left out.
    /// </summary>
    public static JsonObject Complex(IEnumerable<KeyValuePair<string, JsonObject?>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        JsonObject identifier = new() { ["format"] = "complex" };
        foreach ((string name, JsonObject? member) in members)
        {
            if (member is not null)
            {
                identifier[name] = member.DeepClone();
            }
        }
        return identifier;
    }

    /// <summary>
    /// A subject made up of a user and a session.
    /// </summary>
    /// <param name="user">the identifier of the user, may be <see langword="null"/></param>
    /// <param name="session">the identifier of the session, may be <see langword="null"/></param>
    public static JsonObject Complex(JsonObject? user, JsonObject? session)
    {
        JsonObject identifier = new() { ["format"] = "complex" };
        if (user is not null)
        {
            identifier["user"] = user.DeepClone();
        }
        if (session is not null)
        {
            identifier["session"] = session.DeepClone();
        }
        return identifier;
    }

    private static JsonObject Identifier(string format, params (string Name, string Value)[] members)
    {
        JsonObject identifier = new() { ["format"] = format };
        foreach ((string name, string value) in members)
        {
            identifier[name] = value;
        }
        return identifier;
    }
}
