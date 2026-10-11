using System.Text.Json.Nodes;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// Shows the mirrored directory.
/// </summary>
public static class UsersEndpoints
{
    public static void MapUsers(this WebApplication app)
    {
        app.MapGet("/users", (UserDirectory directory) =>
            Json(200, new JsonArray(directory.All().Select(user => (JsonNode)user.ToJson()).ToArray())));
        app.MapGet("/users/{id}", (string id, UserDirectory directory) =>
            directory.Find(id) is { } user ? Json(200, user.ToJson()) : Results.NotFound());
    }

    internal static IResult Json(int status, JsonNode body)
    {
        return Results.Content(body.ToJsonString(), "application/json", statusCode: status);
    }
}
