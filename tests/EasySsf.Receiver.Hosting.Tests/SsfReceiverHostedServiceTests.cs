using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Hosting;
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
