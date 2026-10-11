using System.Net;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;

namespace EasySsf.Examples.ScimProvisioning.Tests;

/// <summary>
/// Runs the application against the in-process transmitter, without the demo provider, and plays
/// the life of a user.
/// </summary>
public sealed class ExampleScimProvisioningApplicationTests : IAsyncLifetime
{
    private const string Alice = "/Users/2b2f880af6674ac284bae9381673d462";

    private const string AliceId = "2b2f880af6674ac284bae9381673d462";

    private ExampleApp _app = null!;

    public async Task InitializeAsync()
    {
        _app = await ExampleApp.StartAsync(demo: false);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task MirrorsTheLifeOfAUser()
    {
        Transmit(SsfEventTypes.ScimProvCreateFull, Json("""
            {"version": "1", "data": {"userName": "alice", "name": {"givenName": "Alice", "familyName": "Adams"},
             "emails": [{"value": "alice@example.com"}]}}
            """));
        await Eventually.UntilAssertedAsync(() =>
        {
            User? alice = _app.Directory.Find(AliceId);
            Assert.NotNull(alice);
            Assert.Equal("alice", alice.UserName);
            Assert.Equal("Alice Adams", alice.DisplayName);
            Assert.Equal(["alice@example.com"], alice.Emails);
            Assert.Equal("alice", alice.ExternalId);
            Assert.True(alice.Active);
            Assert.Equal("1", alice.Version);
        }, TimeSpan.FromSeconds(10));
        using HttpResponseMessage users = await _app.Client.GetAsync("/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        string body = await users.Content.ReadAsStringAsync();
        Assert.Contains("\"userName\":\"alice\"", body);
        Assert.Contains("\"displayName\":\"Alice Adams\"", body);

        Transmit(SsfEventTypes.ScimProvPatchFull, Json("""
            {"version": "2", "data": {"Operations": [
              {"op": "replace", "path": "displayName", "value": "Alice Baker"},
              {"op": "replace", "value": {"active": false}}]}}
            """));
        await Eventually.UntilAssertedAsync(() =>
        {
            User alice = _app.Directory.Find(AliceId)!;
            Assert.Equal("Alice Baker", alice.DisplayName);
            Assert.False(alice.Active);
            Assert.Equal("2", alice.Version);
        }, TimeSpan.FromSeconds(10));

        Transmit(SsfEventTypes.ScimProvActivate, new JsonObject());
        await Eventually.UntilAssertedAsync(() => Assert.True(_app.Directory.Find(AliceId)!.Active), TimeSpan.FromSeconds(10));

        // a notice carries no data and changes nothing
        Transmit(SsfEventTypes.ScimProvPutNotice, Json("""{"attributes": ["userName"]}"""));
        Transmit(SsfEventTypes.ScimProvDelete, new JsonObject());
        await Eventually.UntilAssertedAsync(() => Assert.Null(_app.Directory.Find(AliceId)), TimeSpan.FromSeconds(10));
        using HttpResponseMessage gone = await _app.Client.GetAsync("/users/" + AliceId);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task IgnoresGroups()
    {
        Transmit(SsfEventTypes.ScimProvCreateFull, "/Groups/176f397ec4c44b94b2cfcb759780b8c2", "crmUsers", Json("""{"data": {"displayName": "crmUsers"}}"""));
        Transmit(SsfEventTypes.ScimProvCreateFull, "/Users/44f6142df96bd6ab61e7521d9", null, Json("""{"data": {"userName": "jdoe"}}"""));
        await Eventually.UntilAssertedAsync(() => Assert.NotNull(_app.Directory.Find("44f6142df96bd6ab61e7521d9")), TimeSpan.FromSeconds(10));
        Assert.Null(_app.Directory.Find("176f397ec4c44b94b2cfcb759780b8c2"));
    }

    private void Transmit(string eventType, JsonObject payload)
    {
        Transmit(eventType, Alice, "alice", payload);
    }

    private void Transmit(string eventType, string uri, string? externalId, JsonObject payload)
    {
        JsonObject claims = _app.Transmitter.SetClaims(eventType, SsfSubjectIdentifiers.Scim(uri, externalId), payload);
        claims["aud"] = _app.Transmitter.Streams[0]["aud"]!.DeepClone();
        _app.Transmitter.QueueSet(_app.Transmitter.SignSet(claims));
    }

    private static JsonObject Json(string text)
    {
        return (JsonObject)JsonNode.Parse(text)!;
    }
}
