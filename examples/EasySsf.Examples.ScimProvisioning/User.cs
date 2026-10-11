using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// A user of the local directory, the mirror of a SCIM <c>User</c> resource (RFC 7643, section
/// 4.1): the attributes this application cares about, and the ETag <c>version</c> the service
/// provider reported with the last event about the resource.
/// </summary>
public sealed record User(string Id, string? ExternalId, string? UserName, string? DisplayName, IReadOnlyList<string> Emails, bool Active, string? Version)
{
    /// <summary>
    /// Builds the user from the <c>data</c> of a <c>prov:create:full</c> or <c>prov:put:full</c>
    /// event, the representation of the resource.
    /// </summary>
    public static User FromScim(string id, string? externalId, JsonElement data, string? version)
    {
        bool active = !(data.TryGetProperty("active", out JsonElement activeValue) && activeValue.ValueKind == JsonValueKind.False);
        return new User(id, externalId, String(data, "userName"), DisplayNameOf(data), EmailsOf(Member(data, "emails")), active, version);
    }

    /// <summary>
    /// Applies one attribute of a SCIM PATCH (<c>replace</c> or <c>add</c>) to this user. Attributes
    /// this application does not mirror are ignored.
    /// </summary>
    public User With(string attribute, JsonElement value)
    {
        return attribute switch
        {
            "userName" => this with { UserName = String(value) },
            "displayName" => this with { DisplayName = String(value) },
            "emails" => this with { Emails = EmailsOf(value) },
            "active" => WithActive(value.ValueKind != JsonValueKind.False && !(value.ValueKind == JsonValueKind.String && value.GetString() == "false")),
            _ => this,
        };
    }

    public User WithActive(bool active)
    {
        return this with { Active = active };
    }

    public User WithVersion(string? version)
    {
        return (version is not null) ? this with { Version = version } : this;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["externalId"] = ExternalId,
            ["userName"] = UserName,
            ["displayName"] = DisplayName,
            ["emails"] = new JsonArray(Emails.Select(email => (JsonNode)email).ToArray()),
            ["active"] = Active,
            ["version"] = Version,
        };
    }

    public override string ToString()
    {
        return $"User[id={Id}, externalId={ExternalId}, userName={UserName}, displayName={DisplayName}, emails=[{string.Join(", ", Emails)}], active={Active}, version={Version}]";
    }

    private static string? DisplayNameOf(JsonElement data)
    {
        if (String(data, "displayName") is { } displayName)
        {
            return displayName;
        }
        if (Member(data, "name") is { ValueKind: JsonValueKind.Object } name)
        {
            if (String(name, "formatted") is { } formatted)
            {
                return formatted;
            }
            string? given = String(name, "givenName");
            string? family = String(name, "familyName");
            if (given is not null || family is not null)
            {
                return ((given is not null) ? given + " " : "") + (family ?? "");
            }
        }
        return null;
    }

    /// <summary>
    /// The values of the multi-valued <c>emails</c> attribute: complex values with a <c>value</c>
    /// member, or plain strings.
    /// </summary>
    private static IReadOnlyList<string> EmailsOf(JsonElement? value)
    {
        List<string> emails = [];
        if (value is { ValueKind: JsonValueKind.Array } values)
        {
            foreach (JsonElement email in values.EnumerateArray())
            {
                if (email.ValueKind == JsonValueKind.Object && String(email, "value") is { } address)
                {
                    emails.Add(address);
                }
                else if (email.ValueKind == JsonValueKind.String)
                {
                    emails.Add(email.GetString()!);
                }
            }
        }
        return emails;
    }

    private static JsonElement? Member(JsonElement data, string name)
    {
        return (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out JsonElement value)) ? value : null;
    }

    private static string? String(JsonElement data, string name)
    {
        return Member(data, name) is { } value ? String(value) : null;
    }

    private static string? String(JsonElement value)
    {
        return (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) ? value.GetString() : null;
    }
}
