using System.Text.Json.Nodes;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// The API of the demo SCIM service provider, a tiny stand-in for the <c>/Users</c> endpoint of a
/// SCIM server: each request changes a user and transmits the SCIM Event about it, which the
/// receiver of this application mirrors into <c>/users</c> on its next poll. Every response is the
/// SET transmitted, so that the event is visible next to its cause. See
/// <c>example-scim-provisioning.http</c>.
/// </summary>
public static class DemoScimEndpoints
{
    public static void MapDemoScimProvider(this WebApplication app)
    {
        RouteGroupBuilder scim = app.MapGroup("/demo/scim");
        ILogger logger = app.Logger;

        // creates a user from its SCIM representation: feed:add and prov:create:full; the externalId
        // of the representation, if any, goes into the subject of the events
        scim.MapPost("/Users", async (HttpContext context, DemoScimProvider provider) =>
        {
            JsonObject user = await Body(context);
            string uri = DemoScimProvider.NewUserUri();
            string? externalId = user["externalId"]?.GetValue<string>();
            Narrate(logger, $"POST /demo/scim/Users: {user["userName"]?.GetValue<string>() ?? uri} is created and joins the feed");
            return Transmitting(() =>
            {
                JsonObject set = provider.Create(uri, externalId, user);
                context.Response.Headers.Location = "/demo/scim" + uri;
                return UsersEndpoints.Json(201, set);
            });
        });
        // replaces a user: prov:put:full
        scim.MapPut("/Users/{id}", async (string id, HttpContext context, DemoScimProvider provider) =>
        {
            JsonObject user = await Body(context);
            Narrate(logger, $"PUT /demo/scim/Users/{id}: the user is replaced");
            return Transmitting(() => UsersEndpoints.Json(200, provider.Replace("/Users/" + id, user)));
        });
        // modifies a user with a SCIM PatchOp: prov:patch:full, or with ?mode=notice a prov:patch:notice
        // that only names the paths of the operations
        scim.MapPatch("/Users/{id}", async (string id, HttpContext context, DemoScimProvider provider) =>
        {
            JsonObject patchOp = await Body(context);
            string uri = "/Users/" + id;
            if (context.Request.Query["mode"].FirstOrDefault() == "notice")
            {
                Narrate(logger, $"PATCH /demo/scim/Users/{id}?mode=notice: the user is modified, reported without data");
                return Transmitting(() => UsersEndpoints.Json(200, provider.PatchNotice(uri, Paths(patchOp))));
            }
            Narrate(logger, $"PATCH /demo/scim/Users/{id}: the user is modified");
            return Transmitting(() => UsersEndpoints.Json(200, provider.Patch(uri, patchOp)));
        });
        scim.MapPost("/Users/{id}/activate", (string id, DemoScimProvider provider) =>
        {
            Narrate(logger, $"POST /demo/scim/Users/{id}/activate: the user is activated");
            return Transmitting(() => UsersEndpoints.Json(200, provider.Activate("/Users/" + id)));
        });
        scim.MapPost("/Users/{id}/deactivate", (string id, DemoScimProvider provider) =>
        {
            Narrate(logger, $"POST /demo/scim/Users/{id}/deactivate: the user is deactivated");
            return Transmitting(() => UsersEndpoints.Json(200, provider.Deactivate("/Users/" + id)));
        });
        // deletes a user: prov:delete
        scim.MapDelete("/Users/{id}", (string id, DemoScimProvider provider) =>
        {
            Narrate(logger, $"DELETE /demo/scim/Users/{id}: the user is deleted");
            return Transmitting(() => UsersEndpoints.Json(200, provider.Delete("/Users/" + id)));
        });
        // the SETs transmitted so far, oldest first, each as the compact JWT and its decoded claims
        scim.MapGet("/events", (DemoScimProvider provider) =>
            UsersEndpoints.Json(200, new JsonArray(provider.Transmitted().Select(set => (JsonNode)set).ToArray())));
    }

    private static IResult Transmitting(Func<IResult> transmit)
    {
        try
        {
            return transmit();
        }
        catch (InvalidOperationException ex)
        {
            return UsersEndpoints.Json(503, new JsonObject { ["error"] = ex.Message });
        }
    }

    private static async Task<JsonObject> Body(HttpContext context)
    {
        JsonNode? body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        return body as JsonObject ?? [];
    }

    private static void Narrate(ILogger logger, string whatHappens)
    {
        // the line break leaves an empty line in the log before the headline
        logger.LogInformation("\n### {WhatHappens}", whatHappens);
    }

    private static List<string> Paths(JsonObject patchOp)
    {
        List<string> paths = [];
        if (patchOp["Operations"] is JsonArray operations)
        {
            foreach (JsonNode? operation in operations)
            {
                if (operation is not JsonObject map)
                {
                    continue;
                }
                if (map["path"] is JsonValue path && path.TryGetValue(out string? pathValue))
                {
                    paths.Add(pathValue);
                }
                else if (map["value"] is JsonObject attributes)
                {
                    paths.AddRange(attributes.Select(attribute => attribute.Key));
                }
            }
        }
        return paths;
    }
}
