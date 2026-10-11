using System.Net;
using System.Text.Json.Nodes;

using EasySsf.Receiver.AspNetCore;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.AspNetCore.Tests;

/// <summary>
/// The push endpoint of an application that started while the transmitter was not available.
/// </summary>
public sealed class PushEndpointTests : IAsyncLifetime
{
    private readonly TestTransmitter _transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.None);

    private ReceiverApp _app = null!;

    public async Task InitializeAsync()
    {
        _app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
            ["EasySsf:Receiver:Push:ExpectedAuthHeader"] = "Bearer push-secret",
        }, transmitters: _transmitter);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _transmitter.Dispose();
    }

    [Fact]
    public async Task AsksForRedeliveryWhileMetadataIsUnavailableAndAcceptsSetsAfterwards()
    {
        using HttpResponseMessage unavailable = await _app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("session-1")), "Bearer push-secret");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

        _transmitter.PublishMetadata(TestTransmitter.MetadataLocation.OidcStyle);
        using HttpResponseMessage accepted = await _app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("session-1")), "Bearer push-secret");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(0, accepted.Content.Headers.ContentLength ?? 0);

        // a stream verification event is acknowledged like any other SET
        string verification = _transmitter.SignSet(_transmitter.SetClaims("SsfStreamVerification", Opaque("stream-1")));
        using HttpResponseMessage verified = await _app.PushAsync(verification, "Bearer push-secret");
        Assert.Equal(HttpStatusCode.Accepted, verified.StatusCode);
    }

    [Fact]
    public async Task RejectsTransmitterThatDoesNotAuthenticateWithAnErrorDocument()
    {
        using HttpResponseMessage response = await _app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("session-1")), "Bearer wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("en", response.Content.Headers.ContentLanguage.Single());
        JsonObject error = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("authentication_failed", error["err"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectsInvalidSetWithAnErrorDocument()
    {
        _transmitter.PublishMetadata(TestTransmitter.MetadataLocation.Ssf);
        using HttpResponseMessage response = await _app.PushAsync("not-a-set", "Bearer push-secret");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject error = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid_request", error["err"]!.GetValue<string>());
        Assert.True(error.ContainsKey("description"));
    }

    [Fact]
    public async Task RejectsOversizedBody()
    {
        using HttpResponseMessage response = await _app.PushAsync(new string('x', 300 * 1024), "Bearer push-secret");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task OnlyAcceptsPost()
    {
        using HttpResponseMessage response = await _app.Client.GetAsync("/ssf/push");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task UnknownPathIsNotFound()
    {
        using HttpResponseMessage response = await _app.PushAsync("x", path: "/other");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// The push endpoint at a configured path and at a path given to <c>MapSsfPush</c>.
/// </summary>
public sealed class PushEndpointPathTests : IDisposable
{
    private readonly TestTransmitter _transmitter = new();

    public void Dispose()
    {
        _transmitter.Dispose();
    }

    [Fact]
    public async Task EndpointPathIsConfigurable()
    {
        await using ReceiverApp app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
            ["EasySsf:Receiver:Push:EndpointPath"] = "/events",
        }, transmitters: _transmitter);
        using HttpResponseMessage accepted = await app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("s")), path: "/events");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using HttpResponseMessage notFound = await app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("s")), path: "/ssf/push");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task PatternGivenToMapWins()
    {
        await using ReceiverApp app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
        }, map: web => web.MapSsfPush("/ssf/inbox"), transmitters: _transmitter);
        using HttpResponseMessage accepted = await app.PushAsync(_transmitter.Set("CaepSessionRevoked", Opaque("s")), path: "/ssf/inbox");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }
}
