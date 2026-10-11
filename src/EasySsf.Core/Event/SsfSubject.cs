using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Support;

namespace EasySsf.Core.Event;

/// <summary>
/// The subject of a SET (its <c>sub_id</c> claim): a simple subject identifier (RFC 9493), or a
/// complex subject whose members (<c>user</c>, <c>session</c>, <c>device</c>, <c>tenant</c>, ...)
/// are identifiers.
/// <para>
/// <see cref="UserIdentifier"/> and <see cref="Session"/> are the two members the receiver acts
/// on, and <see cref="Issuer"/>, <see cref="Subject"/>, <see cref="SessionId"/>,
/// <see cref="Email"/> and <see cref="OpaqueId"/> are shortcuts to their most common values.
/// Everything else is available through <see cref="Member"/> and <see cref="Raw"/>.
/// </para>
/// <para>
/// Keycloak reports the revocation of all sessions of a user with a <c>session</c> member whose
/// identifier is <see cref="AllSessions"/>. Such a subject is resolved like one that names only
/// the user: it has no <see cref="Session"/>.
/// </para>
/// </summary>
public sealed record SsfSubject
{
    /// <summary>
    /// Session identifier Keycloak uses to refer to all sessions of a user.
    /// </summary>
    public const string AllSessions = "ALL";

    public const string UserMember = "user";

    public const string SessionMember = "session";

    public const string DeviceMember = "device";

    public const string ApplicationMember = "application";

    public const string TenantMember = "tenant";

    public const string OrgUnitMember = "org_unit";

    public const string GroupMember = "group";

    /// <summary>
    /// The members of a complex subject this class gives access to, in the order of the SSF
    /// specification: <c>user</c>, <c>session</c>, <c>device</c>, <c>application</c>,
    /// <c>tenant</c>, <c>org_unit</c>, <c>group</c>.
    /// </summary>
    public static IReadOnlyList<string> Members { get; } = new ReadOnlyCollection<string>(
        [UserMember, SessionMember, DeviceMember, ApplicationMember, TenantMember, OrgUnitMember, GroupMember]);

    private static readonly ReadOnlyDictionary<string, SsfSubjectIdentifier> NoMembers = new(new Dictionary<string, SsfSubjectIdentifier>());

    /// <summary>
    /// The subject of a SET that carried none.
    /// </summary>
    public static SsfSubject Empty { get; } = new(SsfJson.EmptyObject, null, NoMembers);

    /// <param name="raw">the subject identifier as received, a JSON object; empty if the SET carried none</param>
    /// <param name="identifier">the identifier of a simple subject, <see langword="null"/> for a complex subject or none</param>
    /// <param name="members">the members of a complex subject by name, empty for a simple subject</param>
    public SsfSubject(JsonElement raw, SsfSubjectIdentifier? identifier,
            IReadOnlyDictionary<string, SsfSubjectIdentifier>? members)
    {
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            raw = SsfJson.EmptyObject;
        }
        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("raw must be a JSON object", nameof(raw));
        }
        Raw = raw.Clone();
        Identifier = identifier;
        MemberIdentifiers = (members is null || members.Count == 0) ? NoMembers
            : new ReadOnlyDictionary<string, SsfSubjectIdentifier>(new OrderedDictionary<string, SsfSubjectIdentifier>(members, StringComparer.Ordinal));
    }

    /// <summary>
    /// The subject identifier as received, a JSON object; empty if the SET carried none.
    /// </summary>
    public JsonElement Raw { get; }

    /// <summary>
    /// The identifier of a simple subject, <see langword="null"/> for a complex subject or none.
    /// </summary>
    public SsfSubjectIdentifier? Identifier { get; }

    /// <summary>
    /// The members of a complex subject by name, in the order received; empty for a simple
    /// subject.
    /// </summary>
    public IReadOnlyDictionary<string, SsfSubjectIdentifier> MemberIdentifiers { get; }

    /// <summary>
    /// Resolves the given subject identifier.
    /// </summary>
    /// <param name="subjectId">the <c>sub_id</c> claim, may be <see langword="null"/></param>
    public static SsfSubject From(JsonElement? subjectId)
    {
        if (subjectId is not { ValueKind: JsonValueKind.Object } element || element.IsEmptyObject())
        {
            return Empty;
        }
        string? format = element.StringMember("format");
        // Early SSF drafts used complex subjects without a format member
        bool complex = format == "complex" || (format is null && HasMembers(element));
        if (!complex)
        {
            return new SsfSubject(element, SsfSubjectIdentifier.From(element), null);
        }
        OrderedDictionary<string, SsfSubjectIdentifier> members = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                    && SsfSubjectIdentifier.From(property.Value) is { } identifier)
            {
                members[property.Name] = identifier;
            }
        }
        if (members.TryGetValue(SessionMember, out SsfSubjectIdentifier? session) && session.IsOpaque
                && session.Value == AllSessions && members.ContainsKey(UserMember))
        {
            members.Remove(SessionMember);
        }
        return new SsfSubject(element, null, members);
    }

    /// <summary>
    /// Resolves the given subject identifier.
    /// </summary>
    /// <param name="subjectId">the <c>sub_id</c> claim, may be <see langword="null"/></param>
    public static SsfSubject From(JsonNode? subjectId)
    {
        return (subjectId is JsonObject) ? From(SsfJson.ToElement(subjectId)) : Empty;
    }

    /// <summary>
    /// The names of the members of a complex subject, whether or not <see cref="Member"/> can read
    /// them; empty for a simple subject.
    /// </summary>
    public IReadOnlyList<string> MemberNames()
    {
        if (Identifier is not null)
        {
            return [];
        }
        List<string> names = [];
        foreach (JsonProperty property in Raw.EnumerateObject())
        {
            if (property.Name != "format" && property.Value.ValueKind == JsonValueKind.Object)
            {
                names.Add(property.Name);
            }
        }
        return names.AsReadOnly();
    }

    private static bool HasMembers(JsonElement subjectId)
    {
        foreach (JsonProperty property in subjectId.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether this is a complex subject.
    /// </summary>
    public bool IsComplex => MemberIdentifiers.Count > 0;

    /// <returns>
    /// the member of a complex subject with the given name, <see langword="null"/> if absent
    /// </returns>
    public SsfSubjectIdentifier? Member(string name)
    {
        return MemberIdentifiers.TryGetValue(name, out SsfSubjectIdentifier? member) ? member : null;
    }

    public SsfSubjectIdentifier? User => Member(UserMember);

    public SsfSubjectIdentifier? Session => Member(SessionMember);

    public SsfSubjectIdentifier? Device => Member(DeviceMember);

    public SsfSubjectIdentifier? Application => Member(ApplicationMember);

    public SsfSubjectIdentifier? Tenant => Member(TenantMember);

    public SsfSubjectIdentifier? OrgUnit => Member(OrgUnitMember);

    public SsfSubjectIdentifier? Group => Member(GroupMember);

    /// <summary>
    /// The identifier that names the user: the <c>user</c> member of a complex subject, or the
    /// identifier of a simple subject unless it is <c>opaque</c>, which may as well name a session
    /// (see <see cref="OpaqueId"/>).
    /// </summary>
    /// <returns>the identifier, <see langword="null"/> if the subject does not name a user</returns>
    public SsfSubjectIdentifier? UserIdentifier
    {
        get
        {
            if (Identifier is not null)
            {
                return Identifier.IsOpaque ? null : Identifier;
            }
            return User;
        }
    }

    /// <summary>
    /// The issuer of the user, from an <c>iss_sub</c> identifier, may be <see langword="null"/>.
    /// </summary>
    public string? Issuer => UserIdentifier?.Issuer;

    /// <summary>
    /// The identifier of the user at the issuer: <c>sub</c> of an <c>iss_sub</c> identifier, or
    /// the <c>id</c> of an <c>opaque</c> user of a complex subject, may be <see langword="null"/>.
    /// </summary>
    public string? Subject
    {
        get
        {
            SsfSubjectIdentifier? user = UserIdentifier;
            return (user is not null && (user.IsIssSub || user.IsOpaque)) ? user.Value : null;
        }
    }

    /// <summary>
    /// The identifier of the session, may be <see langword="null"/>.
    /// </summary>
    public string? SessionId => Session?.Value;

    /// <summary>
    /// The email address of the user, from an <c>email</c> identifier, may be <see langword="null"/>.
    /// </summary>
    public string? Email
    {
        get
        {
            SsfSubjectIdentifier? user = UserIdentifier;
            return (user is not null && user.IsEmail) ? user.Value : null;
        }
    }

    /// <summary>
    /// The identifier of a simple <c>opaque</c> subject, which does not tell whether it names a
    /// user or a session, may be <see langword="null"/>.
    /// </summary>
    public string? OpaqueId => (Identifier is not null && Identifier.IsOpaque) ? Identifier.Value : null;

    /// <summary>
    /// Whether the SET carried no subject identifier that could be resolved.
    /// </summary>
    public bool IsEmpty => Identifier is null && MemberIdentifiers.Count == 0;

    /// <summary>
    /// Whether this subject identifies a user (as opposed to only a session).
    /// </summary>
    public bool HasUser => UserIdentifier is not null;

    /// <summary>
    /// Returns this subject without its session, i.e. widened to the user.
    /// </summary>
    public SsfSubject WithoutSession()
    {
        if (Session is null)
        {
            return this;
        }
        OrderedDictionary<string, SsfSubjectIdentifier> members = new(MemberIdentifiers, StringComparer.Ordinal);
        members.Remove(SessionMember);
        return new SsfSubject(Raw, Identifier, members);
    }

    public bool Equals(SsfSubject? other)
    {
        if (other is null)
        {
            return false;
        }
        if (!Equals(Identifier, other.Identifier) || MemberIdentifiers.Count != other.MemberIdentifiers.Count
                || !JsonElement.DeepEquals(Raw, other.Raw))
        {
            return false;
        }
        foreach ((string name, SsfSubjectIdentifier member) in MemberIdentifiers)
        {
            if (!Equals(member, other.Member(name)))
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Identifier);
        hash.Add(MemberIdentifiers.Count);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Raw.GetRawText();
    }
}
