using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace EasySsf.Examples.ScimProvisioning.Tests;

/// <summary>
/// The demo provider: its API transmits SCIM Events that the receiver of the same application
/// mirrors, the walk-through of <c>example-scim-provisioning.http</c>.
/// </summary>
public sealed class DemoScimProviderTests : IAsyncLifetime
{
    private ExampleApp _app = null!;

    public async Task InitializeAsync()
    {
        _app = await ExampleApp.StartAsync(demo: true, autoplay: false);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task RequestsToTheDemoProviderAreMirroredByTheReceiver()
    {
        using HttpResponseMessage created = await _app.Client.PostAsync("/demo/scim/Users",
            Content("""{"userName": "carol", "externalId": "carol", "name": {"givenName": "Carol", "familyName": "Clark"}}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonObject claims = (JsonObject)JsonNode.Parse(await created.Content.ReadAsStringAsync())!["claims"]!;
        JsonObject subject = (JsonObject)claims["sub_id"]!;
        Assert.Equal("scim", subject["format"]!.GetValue<string>());
        Assert.Equal("carol", subject["externalId"]!.GetValue<string>());
        string uri = subject["uri"]!.GetValue<string>();
        string id = uri["/Users/".Length..];
        Assert.Equal(["urn:ietf:params:scim:event:feed:add", "urn:ietf:params:scim:event:prov:create:full"], ((JsonObject)claims["events"]!).Select(e => e.Key));
        Assert.Equal("/demo/scim" + uri, created.Headers.Location?.ToString());

        await Eventually.UntilAssertedAsync(() =>
        {
            User? carol = _app.Directory.Find(id);
            Assert.NotNull(carol);
            Assert.Equal("Carol Clark", carol.DisplayName);
            Assert.Equal("carol", carol.ExternalId);
        }, TimeSpan.FromSeconds(10));

        using HttpResponseMessage notice = await _app.Client.PatchAsync($"/demo/scim/Users/{id}?mode=notice",
            Content("""{"Operations": [{"op": "replace", "path": "phoneNumbers", "value": []}]}"""));
        Assert.Equal(HttpStatusCode.OK, notice.StatusCode);
        using HttpResponseMessage deactivated = await _app.Client.PostAsync($"/demo/scim/Users/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        await Eventually.UntilAssertedAsync(() => Assert.False(_app.Directory.Find(id)!.Active), TimeSpan.FromSeconds(10));
        Assert.Equal("1", _app.Directory.Find(id)!.Version);

        using HttpResponseMessage deleted = await _app.Client.DeleteAsync($"/demo/scim/Users/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await Eventually.UntilAssertedAsync(() => Assert.Null(_app.Directory.Find(id)), TimeSpan.FromSeconds(10));

        JsonArray events = (JsonArray)JsonNode.Parse(await _app.Client.GetStringAsync("/demo/scim/events"))!;
        Assert.Equal(4, events.Count);
        Assert.All(events, set => Assert.Contains('.', set!["set"]!.GetValue<string>()));
        using HttpResponseMessage gone = await _app.Client.GetAsync("/users/" + id);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    private static StringContent Content(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
