using System.Text.Json;

using EasySsf.Core.Event;

namespace EasySsf.Core.Scim;

/// <summary>
/// The subject of a SCIM Event (RFC 9967, section 2.1): a SCIM resource named by a <c>scim</c>
/// subject identifier. The identifier carries the relative <c>uri</c> of the resource (its
/// resource type endpoint plus its <c>id</c>, for example
/// <c>/Users/2b2f880af6674ac284bae9381673d462</c>), the <c>externalId</c> and <c>id</c> of the
/// resource if known, and possibly further attributes of the resource that are unique at the SCIM
/// service provider, such as <c>userName</c>.
/// </summary>
public sealed record SsfScimSubject
{
    /// <param name="identifier">the <c>scim</c> subject identifier as received</param>
    /// <exception cref="ArgumentException">if the identifier is not of format <c>scim</c></exception>
    public SsfScimSubject(SsfSubjectIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.IsScim)
        {
            throw new ArgumentException(
                $"A SCIM subject needs a subject identifier of format 'scim', not '{identifier.Format}'", nameof(identifier));
        }
        Identifier = identifier;
    }

    /// <summary>
    /// The <c>scim</c> subject identifier as received.
    /// </summary>
    public SsfSubjectIdentifier Identifier { get; }

    /// <param name="identifier">a subject identifier, may be <see langword="null"/></param>
    /// <returns>the SCIM subject, <see langword="null"/> if the identifier is not of format <c>scim</c></returns>
    public static SsfScimSubject? From(SsfSubjectIdentifier? identifier)
    {
        return (identifier is not null && identifier.IsScim) ? new SsfScimSubject(identifier) : null;
    }

    /// <param name="subject">the subject of a SET, may be <see langword="null"/></param>
    /// <returns>
    /// the SCIM subject, <see langword="null"/> if the subject is not a <c>scim</c> identifier (nor
    /// a complex subject whose <c>user</c> is one)
    /// </returns>
    public static SsfScimSubject? From(SsfSubject? subject)
    {
        return From(subject?.UserIdentifier);
    }

    /// <summary>
    /// The relative path of the resource at the SCIM service provider, which RFC 9967 requires;
    /// <see langword="null"/> if the transmitter left it out nevertheless.
    /// </summary>
    public string? Uri => Identifier.Claim("uri");

    /// <summary>
    /// The resource type endpoint of the <see cref="Uri"/>, for example <c>Users</c> or
    /// <c>Groups</c>; <see langword="null"/> if the uri has no such segment.
    /// </summary>
    public string? ResourceType
    {
        get
        {
            List<string> segments = Segments();
            return (segments.Count >= 2) ? segments[^2] : null;
        }
    }

    /// <summary>
    /// The <c>id</c> of the resource: the <c>id</c> attribute of the identifier or, if absent, the
    /// last segment of the <see cref="Uri"/>; <see langword="null"/> if there is neither.
    /// </summary>
    public string? Id
    {
        get
        {
            string? id = Identifier.Claim("id");
            if (id is not null)
            {
                return id;
            }
            List<string> segments = Segments();
            return (segments.Count == 0) ? null : segments[^1];
        }
    }

    /// <summary>
    /// The <c>externalId</c> of the resource, the identifier the receiver's domain knows it by;
    /// <see langword="null"/> if not known.
    /// </summary>
    public string? ExternalId => Identifier.Claim("externalId");

    /// <summary>
    /// A further attribute of the identifier, such as <c>userName</c>; <see langword="null"/> if
    /// absent or not a non-empty string.
    /// </summary>
    public string? Attribute(string name)
    {
        return Identifier.Claim(name);
    }

    /// <summary>
    /// All members of the identifier as received, including <c>format</c>.
    /// </summary>
    public JsonElement Claims => Identifier.Claims;

    private List<string> Segments()
    {
        string? uri = Uri;
        if (uri is null)
        {
            return [];
        }
        int query = uri.IndexOf('?');
        if (query >= 0)
        {
            uri = uri[..query];
        }
        return uri.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public override string ToString()
    {
        return Identifier.ToString();
    }
}
