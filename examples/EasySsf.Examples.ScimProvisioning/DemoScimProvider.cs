using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Testing;

namespace EasySsf.Examples.ScimProvisioning;

/// <summary>
/// Plays the SCIM service provider: every operation on a user becomes a SCIM Event (RFC 9967) that
/// the demo transmitter delivers to the receiver of this very application. The operations are
/// driven by the <c>/demo/scim</c> endpoints and, unless <c>Demo:Autoplay</c> is
/// <see langword="false"/>, by <see cref="PlayAsync"/>, which tells the life of two users once the
/// receiver has registered its stream, one SET every few seconds.
/// <para>
/// The SETs transmitted are kept, decoded, for <c>GET /demo/scim/events</c>: that is what a SCIM
/// Event looks like on the wire.
/// </para>
/// </summary>
public sealed class DemoScimProvider(TestTransmitter transmitter, IConfiguration configuration, ILogger<DemoScimProvider> logger) : IHostedService
{
    public const string Alice = "/Users/2b2f880af6674ac284bae9381673d462";

    public const string Bob = "/Users/c3a6e1f0b2d94f0e9a7c5d8b6e4f2a10";

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(4);

    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string> _externalIds = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<JsonObject> _transmitted = new();

    private readonly CancellationTokenSource _stopping = new();

    private Task? _play;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue("Demo:Autoplay", true))
        {
            _play = Task.Run(() => PlayAsync(_stopping.Token), CancellationToken.None);
        }
        else
        {
            logger.LogInformation("The demo SCIM service provider waits for requests to /demo/scim, Demo:Autoplay is off");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_play is not null)
        {
            try
            {
                await _play.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }
    }

    /// <summary>
    /// The life of two users: created, modified, replaced, deactivated, deleted. The resources and
    /// the patch are written as JSON here so that the SCIM payloads are easy to read; an application
    /// would build them from its own objects.
    /// </summary>
    public async Task PlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (transmitter.Streams.Count == 0)
            {
                await Task.Delay(200, cancellationToken);
            }
            logger.LogInformation("The receiver registered its stream, the demo SCIM service provider starts");
            Narrate("Alice is created at the service provider and joins the feed: feed:add and prov:create:full in one SET, with her representation as data");
            Create(Alice, "alice", Json("""
                {
                  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
                  "userName": "alice",
                  "displayName": "Alice Adams",
                  "emails": [{"type": "work", "value": "alice@example.com", "primary": true}],
                  "active": true
                }
                """));
            await Task.Delay(Pause, cancellationToken);
            Narrate("Bob is created as well");
            Create(Bob, "bob", Json("""
                {
                  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
                  "userName": "bob",
                  "displayName": "Bob Brown",
                  "emails": [{"type": "work", "value": "bob@example.com", "primary": true}],
                  "active": true
                }
                """));
            await Task.Delay(Pause, cancellationToken);
            Narrate("Alice marries: a SCIM PATCH changes her display name and adds an email, prov:patch:full carries the PatchOp as data");
            Patch(Alice, Json("""
                {
                  "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
                  "Operations": [
                    {"op": "replace", "path": "displayName", "value": "Alice Baker"},
                    {"op": "add", "path": "emails", "value": [
                      {"type": "work", "value": "alice@example.com"},
                      {"type": "home", "value": "alice.baker@home.example"}
                    ]}
                  ]
                }
                """));
            await Task.Delay(Pause, cancellationToken);
            Narrate("Bob is replaced wholesale with a SCIM PUT: prov:put:full carries the new representation");
            Replace(Bob, Json("""
                {
                  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
                  "userName": "robert",
                  "displayName": "Robert Brown",
                  "emails": [{"type": "work", "value": "robert@example.com", "primary": true}],
                  "active": true
                }
                """));
            await Task.Delay(Pause, cancellationToken);
            Narrate("Alice's phone number changes, reported as a notice: prov:patch:notice names the attribute but carries no data, the receiver would have to GET the resource");
            PatchNotice(Alice, ["phoneNumbers"]);
            await Task.Delay(Pause, cancellationToken);
            Narrate("Alice leaves the company and is deactivated: prov:deactivate, an event without payload");
            Deactivate(Alice);
            await Task.Delay(Pause, cancellationToken);
            Narrate("Bob is deleted: prov:delete, which also removes him from the feed");
            Delete(Bob);
            logger.LogInformation("The demo SCIM service provider is done, GET http://localhost:8083/users shows the result once the receiver has polled, GET /demo/scim/events the SETs");
        }
        catch (OperationCanceledException)
        {
            // the application stops
        }
    }

    /// <summary>
    /// Creates the user: <c>feed:add</c> and <c>prov:create:full</c> in one SET, as RFC 9967 allows
    /// for events about the same resource.
    /// </summary>
    /// <returns>the SET transmitted, decoded</returns>
    public JsonObject Create(string uri, string? externalId, JsonObject user)
    {
        if (externalId is not null)
        {
            _externalIds[uri] = externalId;
        }
        JsonObject events = new()
        {
            [SsfEventTypes.ScimFeedAdd] = new JsonObject(),
            [SsfEventTypes.ScimProvCreateFull] = new JsonObject { ["version"] = NextVersion(uri), ["data"] = user.DeepClone() },
        };
        return Transmit(uri, events);
    }

    /// <summary>
    /// Replaces the user with SCIM PUT: <c>prov:put:full</c> with the new representation.
    /// </summary>
    public JsonObject Replace(string uri, JsonObject user)
    {
        return Transmit(uri, new JsonObject { [SsfEventTypes.ScimProvPutFull] = new JsonObject { ["version"] = NextVersion(uri), ["data"] = user.DeepClone() } });
    }

    /// <summary>
    /// Modifies the user with SCIM PATCH: <c>prov:patch:full</c> with the <c>PatchOp</c>.
    /// </summary>
    public JsonObject Patch(string uri, JsonObject patchOp)
    {
        return Transmit(uri, new JsonObject { [SsfEventTypes.ScimProvPatchFull] = new JsonObject { ["version"] = NextVersion(uri), ["data"] = patchOp.DeepClone() } });
    }

    /// <summary>
    /// Reports a modification without its data: <c>prov:patch:notice</c> naming the attributes that
    /// changed.
    /// </summary>
    public JsonObject PatchNotice(string uri, IEnumerable<string> attributes)
    {
        return Transmit(uri, new JsonObject
        {
            [SsfEventTypes.ScimProvPatchNotice] = new JsonObject
            {
                ["version"] = NextVersion(uri),
                ["attributes"] = new JsonArray(attributes.Select(attribute => (JsonNode)attribute).ToArray()),
            },
        });
    }

    public JsonObject Activate(string uri)
    {
        return Transmit(uri, new JsonObject { [SsfEventTypes.ScimProvActivate] = new JsonObject() });
    }

    public JsonObject Deactivate(string uri)
    {
        return Transmit(uri, new JsonObject { [SsfEventTypes.ScimProvDeactivate] = new JsonObject() });
    }

    /// <summary>
    /// Deletes the user: <c>prov:delete</c>, which has no payload attributes and removes the
    /// resource from the feed as well.
    /// </summary>
    public JsonObject Delete(string uri)
    {
        JsonObject set = Transmit(uri, new JsonObject { [SsfEventTypes.ScimProvDelete] = new JsonObject() });
        _versions.TryRemove(uri, out _);
        _externalIds.TryRemove(uri, out _);
        return set;
    }

    /// <summary>
    /// The SETs transmitted so far, oldest first: the compact JWT as <c>set</c> and its decoded
    /// <c>claims</c>.
    /// </summary>
    public IReadOnlyList<JsonObject> Transmitted()
    {
        return _transmitted.Select(set => (JsonObject)set.DeepClone()).ToList();
    }

    public static string NewUserUri()
    {
        return "/Users/" + Guid.NewGuid().ToString("N");
    }

    private JsonObject Transmit(string uri, JsonObject events)
    {
        IReadOnlyList<JsonObject> streams = transmitter.Streams;
        if (streams.Count == 0)
        {
            throw new InvalidOperationException("The receiver has not registered its stream yet, try again in a moment");
        }
        string eventType = events.First().Key;
        _externalIds.TryGetValue(uri, out string? externalId);
        JsonObject claims = transmitter.SetClaims(eventType, SsfSubjectIdentifiers.Scim(uri, externalId), new JsonObject());
        claims["events"] = events;
        // the receiver expects the audience the transmitter assigned to the stream
        claims["aud"] = streams[0]["aud"]!.DeepClone();
        claims["txn"] = Guid.NewGuid().ToString("N");
        string set = transmitter.SignSet(claims);
        transmitter.QueueSet(set);
        JsonObject transmitted = new() { ["set"] = set, ["claims"] = claims.DeepClone() };
        _transmitted.Enqueue(transmitted);
        // the decoded SET in the log: this is what a SCIM Event looks like
        logger.LogInformation("Transmitted SET {Jti} with [{Events}] for {Uri}:\n{Claims}", claims["jti"], string.Join(", ", events.Select(e => SsfEventTypes.AliasOf(e.Key))),
            uri, claims.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (JsonObject)transmitted.DeepClone();
    }

    /// <summary>
    /// A headline in the log for the step that follows, to scroll along in a demo.
    /// </summary>
    private void Narrate(string whatHappens)
    {
        // the line break leaves an empty line in the log before the headline
        logger.LogInformation("\n### {WhatHappens}", whatHappens);
    }

    private string NextVersion(string uri)
    {
        return _versions.AddOrUpdate(uri, 1, (_, version) => version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static JsonObject Json(string text)
    {
        return (JsonObject)JsonNode.Parse(text)!;
    }
}
