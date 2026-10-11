using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Hosting.Tests;

/// <summary>
/// A receiver that registers its own stream at the transmitter and polls it, started and stopped
/// with the host.
/// </summary>
public sealed class SsfReceiverHostedServiceTests : IDisposable
{
    private readonly TestTransmitter _transmitter = new();

    private readonly List<string> _handled = [];

    public void Dispose()
    {
        _transmitter.Dispose();
    }

    [Fact]
    public async Task PushedSetsAreNotVerifiedBeforeTheStreamIsRegistered()
    {
        // the receiver learns the audience from the stream; a SET that arrives before the stream is
        // known cannot be checked and is left to the transmitter's redelivery (503), not accepted
        ServiceCollection services = new();
        services.AddReceiver(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:Stream:Management"] = "Receiver",
            ["EasySsf:Receiver:Push:DeliveryEndpointUrl"] = "https://app.example/ssf/push",
            ["EasySsf:Receiver:Push:ExpectedAuthHeader"] = "Bearer push-secret",
            ["EasySsf:Receiver:OAuth2:TokenUri"] = _transmitter.TokenUri,
            ["EasySsf:Receiver:OAuth2:ClientId"] = TestTransmitter.ClientId,
            ["EasySsf:Receiver:OAuth2:ClientSecret"] = TestTransmitter.ClientSecret,
        }, _transmitter).AddEventHandler(SsfEventHandler.Of((context, _) =>
        {
            _handled.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        }));
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfPushHandler pushHandler = provider.GetRequiredService<SsfPushHandler>();
        _transmitter.Available = false;
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            string set = _transmitter.Set("CaepSessionRevoked", SsfSubjectIdentifiers.Opaque("session-1"));
            SsfPushResponse refused = await pushHandler.HandleAsync("Bearer push-secret", System.Text.Encoding.ASCII.GetBytes(set));
            Assert.Equal(503, refused.Status);
            Assert.Empty(_handled);

            // the stream is registered: SETs with its audience are handled, others rejected
            _transmitter.Available = true;
            SsfReceiverStream receiverStream = provider.GetRequiredService<SsfTransmitters>().Primary!.ReceiverStream;
            await Eventually.UntilAsync(() => receiverStream.StreamId is not null);
            SsfPushResponse rejected = await pushHandler.HandleAsync("Bearer push-secret", System.Text.Encoding.ASCII.GetBytes(set));
            Assert.Equal(400, rejected.Status);
            Assert.Contains("invalid_audience", rejected.Body);
            JsonObject claims = _transmitter.SetClaims("CaepSessionRevoked", SsfSubjectIdentifiers.Opaque("session-1"));
            claims["aud"] = _transmitter.Streams[0]["aud"]!.DeepClone();
            SsfPushResponse accepted = await pushHandler.HandleAsync("Bearer push-secret", System.Text.Encoding.ASCII.GetBytes(_transmitter.SignSet(claims)));
            Assert.Equal(202, accepted.Status);
            Assert.Equal([claims["jti"]!.GetValue<string>()], _handled);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RegistersThePollStreamOnStartAndPollsIt()
    {
        ServiceCollection services = new();
        services.AddReceiver(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:Stream:Management"] = "Receiver",
            ["EasySsf:Receiver:Stream:EventsRequested:0"] = "CaepSessionRevoked",
            ["EasySsf:Receiver:Stream:Description"] = "poll integration test",
            ["EasySsf:Receiver:Poll:Interval"] = "00:00:00.100",
            ["EasySsf:Receiver:Poll:InitialDelay"] = "00:00:00",
            ["EasySsf:Receiver:OAuth2:TokenUri"] = _transmitter.TokenUri,
            ["EasySsf:Receiver:OAuth2:ClientId"] = TestTransmitter.ClientId,
            ["EasySsf:Receiver:OAuth2:ClientSecret"] = TestTransmitter.ClientSecret,
            ["EasySsf:Receiver:OAuth2:Scopes:0"] = "ssf.read",
        }, _transmitter).AddEventHandler(SsfEventHandler.Of((context, _) =>
        {
            _handled.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        }));
        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            SsfTransmitter transmitter = provider.GetRequiredService<SsfTransmitters>().Primary!;
            SsfReceiverStream receiverStream = transmitter.ReceiverStream;
            await Eventually.UntilAsync(() => receiverStream.StreamId is not null);
            JsonObject stream = Assert.Single(_transmitter.Streams);
            Assert.Equal("poll integration test", stream["description"]!.GetValue<string>());
            Assert.Equal(SsfEventTypes.CaepSessionRevoked, stream["events_requested"]![0]!.GetValue<string>());
            Assert.Equal(SsfDeliveryMethod.Poll.Uri(), stream["delivery"]!["method"]!.GetValue<string>());
            Assert.Equal(_transmitter.PollUri, stream["delivery"]!["endpoint_url"]!.GetValue<string>());

            // the audience of the stream is what the transmitter assigned, the verifier learns it from the registration
            JsonObject claims = _transmitter.SetClaims("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
            claims["aud"] = receiverStream.Audience[0];
            string jti = claims["jti"]!.GetValue<string>();
            _transmitter.QueueSet(_transmitter.SignSet(claims));
            await Eventually.UntilAssertedAsync(() => Assert.Contains(jti, _transmitter.AcknowledgedSets));
            Assert.Equal([jti], _handled);

            // a SET for another stream is rejected and reported
            JsonObject other = _transmitter.SetClaims("CaepSessionRevoked", Opaque("session-2"));
            other["aud"] = "another-receiver/another-stream";
            string otherJti = other["jti"]!.GetValue<string>();
            _transmitter.QueueSet(_transmitter.SignSet(other));
            await Eventually.UntilAssertedAsync(() => Assert.Contains(otherJti, _transmitter.ReportedErrors.Keys));
            Assert.Equal("invalid_audience", _transmitter.ReportedErrors[otherJti]!["err"]!.GetValue<string>());
            Assert.True(transmitter.Poller!.IsRunning);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        Assert.False(provider.GetRequiredService<SsfTransmitters>().Primary!.Poller!.IsRunning);
    }

    [Fact]
    public async Task HostStartsAndStopsTheReceiver()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = _transmitter.Issuer,
            ["EasySsf:Receiver:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:Stream:Id"] = _transmitter.AddStream(Core.Stream.SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null).ToJsonObject())["stream_id"]!.GetValue<string>(),
            ["EasySsf:Receiver:Poll:InitialDelay"] = "00:00:00",
            ["EasySsf:Receiver:Poll:Interval"] = "00:00:00.100",
            ["EasySsf:Receiver:TransmitterAccessToken"] = TestTransmitter.DefaultAccessToken,
        });
        builder.Services.AddLogging();
        builder.AddSsfReceiver();
        builder.Services.UseTransmitters(_transmitter);
        using IHost host = builder.Build();
        await host.StartAsync();
        SsfTransmitter transmitter = host.Services.GetRequiredService<SsfTransmitters>().Primary!;
        await Eventually.UntilAsync(() => transmitter.Poller!.LastSuccessfulPollAt is not null);
        Assert.Equal(SsfStreamRegistrarState.Registered, transmitter.StreamRegistrar!.State);
        await host.StopAsync();
        Assert.False(transmitter.Poller!.IsRunning);
    }
}
