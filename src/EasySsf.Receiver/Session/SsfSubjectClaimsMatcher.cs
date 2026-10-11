using System.Security.Claims;
using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Scim;
using EasySsf.Core.Support;

namespace EasySsf.Receiver.Session;

/// <summary>
/// Decides whether the subject of a security event refers to a user or session that is described by
/// claims, typically the claims of the ID token the user logged in with: <c>sid</c> for a session,
/// <c>iss</c> and <c>sub</c>, <c>email</c> or <c>phone_number</c> for a user.
/// <para>
/// An <c>iss_sub</c> identifier names the user at an issuer, so both have to match: claims without
/// an <c>iss</c> never match such a subject. A session is matched by its <c>sid</c> and, if the
/// event names the issuer of the session or of its user, by that issuer as well.
/// </para>
/// <para>
/// A <c>scim</c> identifier (RFC 9967) names a resource of a SCIM service provider, whose identifiers
/// an OpenID Provider may or may not share. Which attributes of the resource are compared with which
/// claims is configurable; by default (<see cref="DefaultScimAttributeClaims"/>) the resource
/// matches a user whose <c>sub</c> is its <c>externalId</c> or its <c>id</c>, or whose
/// <c>preferred_username</c> is its <c>userName</c>. A SCIM attribute that is multi-valued, such as
/// <c>emails</c>, matches if any of its values (the <c>value</c> of each entry) does. Values compared
/// with the <c>email</c> claim match ignoring case, all others exactly.
/// </para>
/// <para>
/// A <see cref="ClaimsPrincipal"/> is read by the JWT claim names and by the claim types ASP.NET Core
/// maps them to (<c>sub</c> to <see cref="ClaimTypes.NameIdentifier"/>, <c>email</c> to
/// <see cref="ClaimTypes.Email"/>, ...); the issuer falls back to the <see cref="Claim.Issuer"/> of
/// the subject claim.
/// </para>
/// </summary>
public static class SsfSubjectClaimsMatcher
{
    /// <summary>
    /// The attributes of a <c>scim</c> subject and the claims they are compared with by default:
    /// <c>externalId</c> and <c>id</c> with <c>sub</c>, <c>userName</c> with <c>preferred_username</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultScimAttributeClaims { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["externalId"] = "sub",
        ["id"] = "sub",
        ["userName"] = "preferred_username",
    };

    private static readonly IReadOnlyDictionary<string, string> MappedClaimTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sub"] = ClaimTypes.NameIdentifier,
        ["email"] = ClaimTypes.Email,
        ["name"] = ClaimTypes.Name,
        ["given_name"] = ClaimTypes.GivenName,
        ["family_name"] = ClaimTypes.Surname,
        ["phone_number"] = ClaimTypes.MobilePhone,
    };

    /// <summary>
    /// Whether the subject refers to the authenticated user or session.
    /// </summary>
    /// <param name="subject">the subject of the event</param>
    /// <param name="principal">the user or session</param>
    /// <param name="scimAttributeClaims">the attributes of a <c>scim</c> subject and the claim each is compared with, <see cref="DefaultScimAttributeClaims"/> if <see langword="null"/></param>
    public static bool Matches(SsfSubject subject, ClaimsPrincipal principal, IReadOnlyDictionary<string, string>? scimAttributeClaims = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Matches(subject, name => ClaimValue(principal, name), null, scimAttributeClaims);
    }

    /// <summary>
    /// Whether the subject refers to the user or session described by the claims.
    /// </summary>
    /// <param name="subject">the subject of the event</param>
    /// <param name="claims">the claims of the user or session, by JWT claim name</param>
    /// <param name="name">the name of the user, compared with the subject of the event if the claims have no <c>sub</c>, may be <see langword="null"/></param>
    /// <param name="scimAttributeClaims">the attributes of a <c>scim</c> subject and the claim each is compared with, <see cref="DefaultScimAttributeClaims"/> if <see langword="null"/></param>
    public static bool Matches(SsfSubject subject, IReadOnlyDictionary<string, string?> claims, string? name = null,
            IReadOnlyDictionary<string, string>? scimAttributeClaims = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return Matches(subject, claimName => claims.GetValueOrDefault(claimName), name, scimAttributeClaims);
    }

    /// <summary>
    /// Whether the subject refers to the user or session whose claims the function looks up.
    /// </summary>
    /// <param name="subject">the subject of the event</param>
    /// <param name="claim">looks a claim up by its JWT name, <see langword="null"/> if absent</param>
    /// <param name="name">the name of the user, compared with the subject of the event if the claims have no <c>sub</c>, may be <see langword="null"/></param>
    /// <param name="scimAttributeClaims">the attributes of a <c>scim</c> subject (RFC 9967) and the claim each is compared with, for example <c>externalId</c> with <c>sub</c>; the subject matches if any pair does; <see cref="DefaultScimAttributeClaims"/> if <see langword="null"/></param>
    public static bool Matches(SsfSubject subject, Func<string, string?> claim, string? name = null,
            IReadOnlyDictionary<string, string>? scimAttributeClaims = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(claim);
        scimAttributeClaims ??= DefaultScimAttributeClaims;
        string? sessionId = claim("sid");
        string? userSubject = claim("sub") ?? name;
        SsfSubjectIdentifier? session = subject.Session;
        if (session is not null)
        {
            if (session.Value is null || session.Value != sessionId)
            {
                return false;
            }
            // the session belongs to the issuer the event names, in the session identifier or the
            // user it belongs to; session identifiers are not unique across issuers
            string? issuer = session.Issuer ?? subject.User?.Issuer;
            return issuer is null || issuer == claim("iss");
        }
        SsfSubjectIdentifier? user = subject.UserIdentifier;
        if (user is not null)
        {
            return MatchesUser(user, claim, userSubject, scimAttributeClaims);
        }
        string? opaqueId = subject.OpaqueId;
        if (opaqueId is not null)
        {
            // an opaque identifier does not tell whether it names a session or a user
            return opaqueId == sessionId || opaqueId == userSubject;
        }
        return false;
    }

    /// <summary>
    /// A claim of a principal by its JWT name: the claim with that type, or the one with the type
    /// ASP.NET Core maps it to (<c>sub</c> to <see cref="ClaimTypes.NameIdentifier"/>, ...). The
    /// <c>iss</c> falls back to the <see cref="Claim.Issuer"/> of the subject claim.
    /// </summary>
    /// <returns>the value, <see langword="null"/> if the principal has no such claim</returns>
    public static string? ClaimValue(ClaimsPrincipal principal, string name)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(name);
        string? value = principal.FindFirst(name)?.Value;
        if (value is null && MappedClaimTypes.TryGetValue(name, out string? mapped))
        {
            value = principal.FindFirst(mapped)?.Value;
        }
        if (value is null && name == "iss")
        {
            Claim? subject = principal.FindFirst("sub") ?? principal.FindFirst(ClaimTypes.NameIdentifier);
            if (subject is not null && subject.Issuer != ClaimsIdentity.DefaultIssuer)
            {
                value = subject.Issuer;
            }
        }
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool MatchesUser(SsfSubjectIdentifier user, Func<string, string?> claim, string? userSubject,
            IReadOnlyDictionary<string, string> scimAttributeClaims)
    {
        string? value = user.Value;
        switch (user.Format)
        {
            case SsfSubjectIdentifier.IssSub:
                return value is not null && value == userSubject && user.Issuer is not null && user.Issuer == claim("iss");
            case SsfSubjectIdentifier.Email:
                return value is not null && value.Equals(claim("email"), StringComparison.OrdinalIgnoreCase);
            case SsfSubjectIdentifier.Opaque:
                return value is not null && value == userSubject;
            case SsfSubjectIdentifier.Aliases:
                return user.AliasIdentifiers().Any(alias => MatchesUser(alias, claim, userSubject, scimAttributeClaims));
            case SsfSubjectIdentifier.Scim:
                return MatchesScimResource(SsfScimSubject.From(user)!, claim, userSubject, scimAttributeClaims);
            default:
                // phone_number, account, did, uri and unknown formats: a claim of the same name
                return value is not null && value == claim(user.Format);
        }
    }

    private static bool MatchesScimResource(SsfScimSubject resource, Func<string, string?> claim, string? userSubject,
            IReadOnlyDictionary<string, string> scimAttributeClaims)
    {
        foreach ((string attribute, string claimName) in scimAttributeClaims)
        {
            string? claimValue = (claimName == "sub") ? userSubject : claim(claimName);
            if (claimValue is null)
            {
                continue;
            }
            foreach (string value in ScimAttributeValues(resource, attribute))
            {
                if ((claimName == "email") ? value.Equals(claimValue, StringComparison.OrdinalIgnoreCase) : value == claimValue)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The values of an attribute of a SCIM resource: the string of a single-valued attribute, the
    /// <c>id</c> resolved from the uri if the resource has no <c>id</c> attribute, or the strings and
    /// the <c>value</c> members of a multi-valued attribute such as <c>emails</c>.
    /// </summary>
    private static List<string> ScimAttributeValues(SsfScimSubject resource, string attribute)
    {
        if (attribute == "id")
        {
            return (resource.Id is { } id) ? [id] : [];
        }
        JsonElement? value = resource.Claims.Member(attribute);
        if (value is { ValueKind: JsonValueKind.String } single)
        {
            string text = single.GetString()!;
            return string.IsNullOrWhiteSpace(text) ? [] : [text];
        }
        if (value is not { ValueKind: JsonValueKind.Array } values)
        {
            return [];
        }
        List<string> strings = [];
        foreach (JsonElement element in values.EnumerateArray())
        {
            string? text = (element.ValueKind == JsonValueKind.String) ? element.GetString() : element.StringMember("value");
            if (!string.IsNullOrWhiteSpace(text))
            {
                strings.Add(text);
            }
        }
        return strings;
    }
}
