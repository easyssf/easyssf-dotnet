using System.Text;

using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Hosting.Tests;

public sealed class SsfReceiverHealthCheckTests : IDisposable
{
    private readonly TestTransmitter _keycloak = new();

    private readonly TestTransmitter _okta = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf, "okta.example");

    public void Dispose()
    {
        _keycloak.Dispose();
        _okta.Dispose();
    }

    [Fact]
    public async Task PushReceiverIsDegradedUntilTheFirstSetAndHealthyAfterwards()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak), _keycloak);
        services.AddHealthChecks().AddSsfReceiver();
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfReceiverHealthCheck health = provider.GetRequiredService<SsfReceiverHealthCheck>();
        HealthCheckResult before = health.Check();
        Assert.Equal(HealthStatus.Degraded, before.Status);
        Assert.Equal("push", before.Data["delivery"]);
        Assert.Equal("not retrieved yet", before.Data["metadata"]);
        Assert.Equal("none", before.Data["stream"]);
        Assert.DoesNotContain("streamRegistration", before.Data.Keys);
        Assert.DoesNotContain("lastPoll", before.Data.Keys);

        string set = _keycloak.Set("CaepSessionRevoked", Opaque("session-1"));
        Assert.Equal(202, (await provider.GetRequiredService<SsfPushHandler>().HandleAsync(null, Encoding.UTF8.GetBytes(set))).Status);

        HealthCheckResult after = health.Check();
        Assert.Equal(HealthStatus.Healthy, after.Status);
        Assert.Equal("resolved", after.Data["metadata"]);
        Assert.Equal(_keycloak.JwksUri, after.Data["jwksUri"]);

        // registered with the health checks of the application under its name
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, report.Entries[SsfReceiverDefaults.HealthCheckName].Status);
    }

    [Fact]
    public async Task PollingReceiverIsHealthyWhilePollingAndUnhealthyWhileTheTransmitterIsDown()
    {
        ServiceCollection services = new();
        services.AddReceiver(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _keycloak.Issuer,
            ["EasySsf:Receiver:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:Stream:Management"] = "Receiver",
            ["EasySsf:Receiver:Poll:Interval"] = "00:00:00.100",
            ["EasySsf:Receiver:Poll:InitialDelay"] = "00:00:00",
            ["EasySsf:Receiver:TransmitterAccessToken"] = TestTransmitter.DefaultAccessToken,
        }, _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfReceiverHealthCheck health = provider.GetRequiredService<SsfReceiverHealthCheck>();
        Assert.Equal(HealthStatus.Degraded, health.Check().Status);
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await Eventually.UntilAsync(() => health.Check().Status == HealthStatus.Healthy);
            HealthCheckResult healthy = health.Check();
            Assert.Equal(_keycloak.Issuer, healthy.Data["transmitter"]);
            Assert.Equal("poll", healthy.Data["delivery"]);
            Assert.Equal("resolved", healthy.Data["metadata"]);
            Assert.Equal("registered", healthy.Data["streamRegistration"]);
            Assert.Contains("lastPoll", healthy.Data.Keys);
            Assert.Contains("lastSuccessfulPoll", healthy.Data.Keys);
            Assert.DoesNotContain("pollError", healthy.Data.Keys);
            Assert.DoesNotContain("streamRegistrationError", healthy.Data.Keys);

            _keycloak.Available = false;
            await Eventually.UntilAsync(() => health.Check().Status == HealthStatus.Unhealthy);
            Assert.Contains("pollError", health.Check().Data.Keys);
            _keycloak.Available = true;
            await Eventually.UntilAsync(() => health.Check().Status == HealthStatus.Healthy);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HealthListsEveryTransmitterOfAReceiverWithSeveral()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak,
            ("EasySsf:Receiver:Transmitters:okta:TransmitterIssuer", _okta.Issuer),
            ("EasySsf:Receiver:Transmitters:okta:DeliveryMethod", "Poll"),
            ("EasySsf:Receiver:Transmitters:okta:Stream:Management", "Receiver"),
            ("EasySsf:Receiver:Transmitters:okta:Poll:Interval", "00:00:00.100"),
            ("EasySsf:Receiver:Transmitters:okta:Poll:InitialDelay", "00:00:00"),
            ("EasySsf:Receiver:Transmitters:okta:TransmitterAccessToken", TestTransmitter.DefaultAccessToken)), _keycloak, _okta);
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfReceiverHealthCheck health = provider.GetRequiredService<SsfReceiverHealthCheck>();
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            SsfTransmitters transmitters = provider.GetRequiredService<SsfTransmitters>();
            await Eventually.UntilAsync(() => transmitters.Get("okta")!.Poller!.LastSuccessfulPollAt is not null);
            HealthCheckResult result = health.Check();
            Assert.Equal(["default", "okta"], result.Data.Keys.Order());
            IReadOnlyDictionary<string, object> okta = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(result.Data["okta"]);
            Assert.Equal("poll", okta["delivery"]);
            Assert.Equal("registered", okta["streamRegistration"]);
            Assert.Equal("Healthy", okta["status"]);
            IReadOnlyDictionary<string, object> dflt = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(result.Data["default"]);
            Assert.Equal("push", dflt["delivery"]);
            Assert.Equal(_keycloak.Issuer, dflt["transmitter"]);
            // the push transmitter was not contacted yet
            Assert.Equal(HealthStatus.Degraded, result.Status);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }
}
