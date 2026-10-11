using System.Text;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Hosting.Tests;

/// <summary>
/// The receiver assembled from its options, the way the Java auto-configuration does it.
/// </summary>
public sealed class SsfReceiverServiceCollectionExtensionsTests : IDisposable
{
    private readonly TestTransmitter _keycloak = new();

    private readonly TestTransmitter _okta = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf, "okta.example");

    private readonly List<string> _handled = [];

    public void Dispose()
    {
        _keycloak.Dispose();
        _okta.Dispose();
    }

    [Fact]
    public async Task AssemblesTheDefaultTransmitterAndHandlesPushedSets()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak, ("EasySsf:Receiver:Push:ExpectedAuthHeader", "Bearer push-secret")), _keycloak)
            .AddEventHandler("CaepSessionRevoked", (context, _) =>
            {
                _handled.Add(context.SubjectFor("CaepSessionRevoked").OpaqueId!);
                return Task.CompletedTask;
            });
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfTransmitters transmitters = provider.GetRequiredService<SsfTransmitters>();
        SsfTransmitter transmitter = Assert.Single(transmitters.All);
        Assert.Equal(SsfTransmitter.DefaultName, transmitter.Name);
        Assert.Same(transmitter, transmitters.Primary);
        Assert.Null(transmitter.Poller);
        Assert.Null(transmitter.StreamRegistrar);
        Assert.NotNull(transmitter.StreamClient);
        Assert.Equal("Bearer push-secret", transmitter.PushAuthorizationHeader);
        Assert.IsType<InMemorySsfJtiDedupStore>(provider.GetRequiredService<ISsfJtiDedupStore>());
        // the HTTP client factory brings the meter factory, so metrics are on by default
        Assert.IsType<MeterSsfReceiverMetrics>(provider.GetRequiredService<ISsfReceiverMetrics>());

        SsfPushHandler pushHandler = provider.GetRequiredService<SsfPushHandler>();
        string set = _keycloak.Set("CaepSessionRevoked", Opaque("session-1"));
        Assert.Equal(202, (await pushHandler.HandleAsync("Bearer push-secret", Encoding.UTF8.GetBytes(set))).Status);
        Assert.Equal(202, (await pushHandler.HandleAsync("Bearer push-secret", Encoding.UTF8.GetBytes(set))).Status);
        Assert.Equal(401, (await pushHandler.HandleAsync("Bearer wrong", Encoding.UTF8.GetBytes(set))).Status);
        Assert.Equal(["session-1"], _handled);
    }

    [Fact]
    public void NamedTransmittersWorkWithoutTheDefaultOne()
    {
        ServiceCollection services = new();
        services.AddReceiver(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:Transmitters:a:TransmitterIssuer"] = _keycloak.Issuer,
            ["EasySsf:Receiver:Transmitters:b:TransmitterIssuer"] = _okta.Issuer,
            ["EasySsf:Receiver:Transmitters:b:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:Transmitters:b:Poll:EndpointUrl"] = _okta.PollUri,
            ["EasySsf:Receiver:Transmitters:b:Poll:AutoStartup"] = "false",
        }, _keycloak, _okta);
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfTransmitters transmitters = provider.GetRequiredService<SsfTransmitters>();
        Assert.Equal(["a", "b"], transmitters.All.Select(t => t.Name));
        Assert.Null(transmitters.Primary);
        Assert.NotNull(transmitters.Get("b")!.Poller);
        Assert.False(transmitters.Get("b")!.AutoStartPolling);
        Assert.Null(transmitters.Get("a")!.Poller);
    }

    [Fact]
    public void TransmitterCustomizersApplyToEveryTransmitter()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak, ("EasySsf:Receiver:Transmitters:b:TransmitterIssuer", _okta.Issuer)), _keycloak, _okta)
            .AddTransmitterCustomizer(builder => builder.PushAuthorizationHeader = "Bearer " + builder.Name);
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfTransmitters transmitters = provider.GetRequiredService<SsfTransmitters>();
        Assert.Equal("Bearer default", transmitters.PushAuthorizationHeader(_keycloak.Issuer));
        Assert.Equal("Bearer b", transmitters.PushAuthorizationHeader(_okta.Issuer));
    }

    [Fact]
    public void RejectsTwoTransmittersWithTheSameIssuerAndMissingIssuer()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak, ("EasySsf:Receiver:Transmitters:twin:TransmitterIssuer", _keycloak.Issuer)), _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        Exception ex = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<SsfTransmitters>());
        Assert.Contains("same issuer", ex.Message);

        ServiceCollection empty = new();
        empty.AddReceiver(new Dictionary<string, string?>(), _keycloak);
        using ServiceProvider emptyProvider = empty.BuildServiceProvider();
        ex = Assert.ThrowsAny<Exception>(() => emptyProvider.GetRequiredService<SsfTransmitters>());
        Assert.Contains("EasySsf:Receiver:TransmitterIssuer", ex.Message);
    }

    [Fact]
    public void RegistersConfiguredEventTypeAliases()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak,
            ("EasySsf:Receiver:EventAliases:AcmeLoginHosted", "https://events.acme.example/login-hosted"),
            ("EasySsf:Receiver:Stream:EventsRequested:0", "AcmeLoginHosted")), _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<SsfTransmitters>();
        Assert.Equal("https://events.acme.example/login-hosted", SsfEventTypes.Resolve("AcmeLoginHosted"));
    }

    [Fact]
    public async Task DedupCanBeSwitchedOff()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak, ("EasySsf:Receiver:Dedup:Enabled", "false")), _keycloak)
            .AddEventHandler(SsfEventHandler.Of((context, _) =>
            {
                _handled.Add(context.EventToken.Jti);
                return Task.CompletedTask;
            }));
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfSetProcessor processor = provider.GetRequiredService<SsfSetProcessor>();
        string set = _keycloak.Set("CaepSessionRevoked", Opaque("session-1"));
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync(set));
        Assert.Equal(SsfSetProcessingOutcome.Handled, await processor.ProcessAsync(set));
        Assert.Equal(2, _handled.Count);
    }

    [Fact]
    public void ApplicationCanProvideItsOwnStore()
    {
        ServiceCollection services = new();
        RecordingDedupStore store = new();
        services.AddSingleton<ISsfJtiDedupStore>(store);
        services.AddReceiver(ReceiverServices.Settings(_keycloak), _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(store, provider.GetRequiredService<ISsfJtiDedupStore>());
    }

    [Fact]
    public void MetricsCanBeSwitchedOff()
    {
        ServiceCollection disabled = new();
        disabled.AddReceiver(ReceiverServices.Settings(_keycloak, ("EasySsf:Receiver:Metrics:Enabled", "false")), _keycloak);
        using ServiceProvider disabledProvider = disabled.BuildServiceProvider();
        Assert.Same(NullSsfReceiverMetrics.Instance, disabledProvider.GetRequiredService<ISsfReceiverMetrics>());
    }

    [Fact]
    public void PollClientAllowsForALongPoll()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_keycloak,
            ("EasySsf:Receiver:DeliveryMethod", "Poll"), ("EasySsf:Receiver:Poll:EndpointUrl", _keycloak.PollUri),
            ("EasySsf:Receiver:Poll:LongPolling", "true"), ("EasySsf:Receiver:Poll:LongPollingHold", "00:00:20"),
            ("EasySsf:Receiver:Http:UserAgent", "my-receiver/1.0")), _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(TimeSpan.FromSeconds(5), factory.CreateClient(SsfReceiverDefaults.HttpClientName).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(35), factory.CreateClient(SsfReceiverDefaults.PollHttpClientName).Timeout);
        Assert.Equal("my-receiver/1.0", factory.CreateClient(SsfReceiverDefaults.HttpClientName).DefaultRequestHeaders.UserAgent.ToString());
        Assert.True(provider.GetRequiredService<SsfTransmitters>().Primary!.Poller!.IsLongPolling);
    }

    [Fact]
    public async Task DisabledReceiverStartsNothing()
    {
        ServiceCollection services = new();
        services.AddReceiver(new Dictionary<string, string?> { ["EasySsf:Receiver:Enabled"] = "false" }, _keycloak);
        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            provider.GetRequiredService<SsfReceiverHealthCheck>().Check().Status);
    }

    [Fact]
    public void HostValidatesTheOptionsOnStart()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EasySsf:Receiver:TransmitterIssuer"] = "http://idp.example" });
        builder.AddSsfReceiver();
        using IHost host = builder.Build();
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => host.Start());
        Assert.Contains("must use https", ex.Message);
    }

    private sealed class RecordingDedupStore : ISsfJtiDedupStore
    {
        public ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(SsfJtiClaim.Granted(1));
        }

        public ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}
