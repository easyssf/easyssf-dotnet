using System.Text.Json.Nodes;

using EasySsf.Core.Stream;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Transmitter;

/// <summary>
/// The parts of a transmitter working together: the registrar registers the stream in the
/// background and the poller fetches right after, not an interval later.
/// </summary>
public sealed class SsfTransmitterTests(TestTransmitter transmitter) : IClassFixture<TestTransmitter>
{
    private readonly List<string> _handled = [];

    [Fact]
    public async Task PollerFetchesAsSoonAsTheStreamIsRegistered()
    {
        transmitter.Reset();
        using HttpClient http = transmitter.CreateHttpClient();
        SsfReceiverStream receiverStream = new();
        using SsfTransmitterMetadataResolver metadataResolver = new(transmitter.Issuer, null, http);
        ISsfTransmitterTokenProvider tokenProvider = SsfTransmitterTokenProviders.Fixed(TestTransmitter.DefaultAccessToken);
        SsfStreamClient streamClient = new(http, tokenProvider, metadataResolver);
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(streamClient, receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        using JwtSsfSetVerifier verifier = new(transmitter.Issuer, new Uri(transmitter.JwksUri), http);
        ISsfEventHandler handler = SsfEventHandler.Of((context, _) =>
        {
            _handled.Add(context.EventToken.Jti);
            return Task.CompletedTask;
        });
        SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(10), [handler]);
        // only the wake-up of the registration can make the poller fetch within the test
        using SsfPoller poller = new(http, tokenProvider, () => receiverStream.Configuration?.DeliveryEndpointUrl, processor,
            new SsfPollerOptions { Interval = TimeSpan.FromHours(1), EndpointRetry = TimeSpan.FromHours(1) });
        SsfTransmitter ssfTransmitter = new SsfTransmitterBuilder("test", transmitter.Issuer)
        {
            MetadataResolver = metadataResolver,
            Verifier = verifier,
            TokenProvider = tokenProvider,
            StreamClient = streamClient,
            ReceiverStream = receiverStream,
            StreamRegistrar = registrar,
            Poller = poller,
        }.Build();
        JsonObject claims = transmitter.SetClaims("CaepSessionRevoked", Opaque("session-1"));
        transmitter.QueueSet(transmitter.SignSet(claims));

        ssfTransmitter.Start();
        try
        {
            await Eventually.UntilAssertedAsync(() =>
            {
                Assert.Equal(SsfStreamRegistrarState.Registered, registrar.State);
                Assert.Equal([claims["jti"]!.GetValue<string>()], _handled);
            });
            Assert.Equal(new Uri(transmitter.PollUri), receiverStream.Configuration!.DeliveryEndpointUrl);
            Assert.True(poller.IsRunning);
            Assert.True(registrar.IsRunning);
        }
        finally
        {
            await ssfTransmitter.StopAsync();
        }
        Assert.False(poller.IsRunning);
        Assert.False(registrar.IsRunning);
        Assert.Equal([claims["jti"]!.GetValue<string>()], transmitter.AcknowledgedSets);
    }
}
