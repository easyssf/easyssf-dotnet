using System.Net;
using System.Text.Json.Nodes;

using EasySsf.Receiver.Event;
using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.AspNetCore.Tests;

/// <summary>
/// A receiver with two transmitters: the default one, configured at <c>EasySsf:Receiver</c>,
/// delivers by PUSH; a second one, configured by name, is polled.
/// </summary>
public sealed class MultipleTransmittersTests : IAsyncLifetime
{
    private readonly TestTransmitter _keycloak = new();

    private readonly TestTransmitter _okta = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf, "okta.example");

    private readonly List<string> _handled = [];

    private ReceiverApp _app = null!;

    public async Task InitializeAsync()
    {
        _app = await ReceiverApp.StartAsync(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _keycloak.Issuer,
            ["EasySsf:Receiver:ExpectedAudience"] = TestTransmitter.Audience,
            ["EasySsf:Receiver:Push:ExpectedAuthHeader"] = "Bearer keycloak-secret",
            ["EasySsf:Receiver:Transmitters:okta:TransmitterIssuer"] = _okta.Issuer,
            ["EasySsf:Receiver:Transmitters:okta:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:Transmitters:okta:Stream:Management"] = "Receiver",
            ["EasySsf:Receiver:Transmitters:okta:Stream:EventsRequested:0"] = "CaepSessionRevoked",
            ["EasySsf:Receiver:Transmitters:okta:Poll:Interval"] = "00:00:00.100",
            ["EasySsf:Receiver:Transmitters:okta:Poll:InitialDelay"] = "00:00:00",
            ["EasySsf:Receiver:Transmitters:okta:OAuth2:TokenUri"] = _okta.TokenUri,
            ["EasySsf:Receiver:Transmitters:okta:OAuth2:ClientId"] = TestTransmitter.ClientId,
            ["EasySsf:Receiver:Transmitters:okta:OAuth2:ClientSecret"] = TestTransmitter.ClientSecret,
        }, configure: builder =>
        {
            builder.Services.AddMetrics();
            builder.Services.AddSingleton(SsfEventHandler.Of((context, _) =>
            {
                _handled.Add(context.EventToken.Iss + " " + context.SubjectFor("CaepSessionRevoked").OpaqueId);
                return Task.CompletedTask;
            }));
        }, transmitters: [_keycloak, _okta]);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _keycloak.Dispose();
        _okta.Dispose();
    }

    [Fact]
    public void KnowsBothTransmitters()
    {
        SsfTransmitters transmitters = _app.Services.GetRequiredService<SsfTransmitters>();
        Assert.Equal(["default", "okta"], transmitters.All.Select(t => t.Name));
        Assert.Equal(_keycloak.Issuer, transmitters.Primary!.Issuer);
        Assert.NotNull(transmitters.Get("okta")!.Poller);
        Assert.Null(transmitters.Get("default")!.Poller);
    }

    [Fact]
    public async Task PushedSetsAreVerifiedByTheirTransmitter()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _app.PushAsync(Set(_keycloak), "Bearer keycloak-secret")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.PushAsync(Set(_keycloak), "Bearer wrong")).StatusCode);
        // the polled transmitter expects no header; its SETs are still verified by its keys and
        // against the audience of its stream
        SsfReceiverStream oktaStream = await AwaitOktaStreamAsync();
        JsonObject oktaSet = _okta.SetClaims("CaepSessionRevoked", Opaque("session-1"));
        oktaSet["aud"] = oktaStream.Audience[0];
        Assert.Equal(HttpStatusCode.Accepted, (await _app.PushAsync(_okta.SignSet(oktaSet))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.PushAsync(Set(_okta))).StatusCode);
        using TestTransmitter stranger = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf, "stranger.example");
        using HttpResponseMessage response = await _app.PushAsync(Set(stranger));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_issuer", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PolledTransmitterDeliversItsEvents()
    {
        SsfReceiverStream oktaStream = await AwaitOktaStreamAsync();
        JsonObject set = _okta.SetClaims("CaepSessionRevoked", Opaque("session-okta"));
        set["aud"] = oktaStream.Audience[0];
        string jti = set["jti"]!.GetValue<string>();
        _okta.QueueSet(_okta.SignSet(set));
        await EventuallyAsync(() => _okta.AcknowledgedSets.Contains(jti));
        Assert.Contains(_okta.Issuer + " session-okta", _handled);
    }

    [Fact]
    public async Task HealthListsEveryTransmitter()
    {
        SsfTransmitters transmitters = _app.Services.GetRequiredService<SsfTransmitters>();
        await EventuallyAsync(() => transmitters.Get("okta")!.Poller!.LastSuccessfulPollAt is not null);
        HealthReport report = await _app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        HealthReportEntry entry = report.Entries[SsfReceiverDefaults.HealthCheckName];
        Assert.Equal(["default", "okta"], entry.Data.Keys.Order());
        IReadOnlyDictionary<string, object> okta = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(entry.Data["okta"]);
        Assert.Equal("poll", okta["delivery"]);
        Assert.Equal("registered", okta["streamRegistration"]);
        Assert.Equal("Healthy", okta["status"]);
    }

    private async Task<SsfReceiverStream> AwaitOktaStreamAsync()
    {
        SsfReceiverStream stream = _app.Services.GetRequiredService<SsfTransmitters>().Get("okta")!.ReceiverStream;
        await EventuallyAsync(() => stream.StreamId is not null);
        return stream;
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    private static string Set(TestTransmitter transmitter)
    {
        return transmitter.Set("CaepSessionRevoked", Opaque("session-1"));
    }
}
