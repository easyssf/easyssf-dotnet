using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Core.Stream;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

namespace EasySsf.Receiver.Tests.Stream;

public sealed class SsfStreamManagementTests : IClassFixture<TestTransmitter>, IDisposable
{
    private static readonly Uri PushEndpoint = new("https://receiver.example/ssf/push");

    private readonly TestTransmitter _transmitter;

    private readonly HttpClient _http;

    private readonly SsfReceiverStream _receiverStream = new();

    private readonly ClientCredentialsSsfTransmitterTokenProvider _tokenProvider;

    private readonly SsfStreamClient _streamClient;

    public SsfStreamManagementTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _transmitter.Reset();
        _http = transmitter.CreateHttpClient();
        _tokenProvider = new ClientCredentialsSsfTransmitterTokenProvider(_http, new Uri(transmitter.TokenUri),
            TestTransmitter.ClientId, TestTransmitter.ClientSecret);
        _streamClient = new SsfStreamClient(_http, _tokenProvider, new SsfTransmitterMetadataResolver(transmitter.Issuer, null, _http));
    }

    public void Dispose()
    {
        _tokenProvider.Dispose();
        _http.Dispose();
    }

    private SsfStreamClient StreamClient(string accessToken)
    {
        return new SsfStreamClient(_http, SsfTransmitterTokenProviders.Fixed(accessToken),
            new SsfTransmitterMetadataResolver(_transmitter.Issuer, null, _http));
    }

    [Fact]
    public async Task TokenProviderCachesAccessToken()
    {
        Assert.Equal(TestTransmitter.DefaultAccessToken, await _tokenProvider.GetAccessTokenAsync());
        int requests = _transmitter.TokenRequests;
        Assert.Equal(TestTransmitter.DefaultAccessToken, await _tokenProvider.GetAccessTokenAsync());
        Assert.Equal(requests, _transmitter.TokenRequests);
    }

    [Fact]
    public async Task TokenProviderAuthenticatesWithRequestBody()
    {
        SsfClientCredentialsOptions options = new() { AuthenticateWithRequestBody = true };
        options.Scopes.Add("ssf.read");
        options.Scopes.Add("ssf.manage");
        using ClientCredentialsSsfTransmitterTokenProvider provider = new(_http, new Uri(_transmitter.TokenUri),
            TestTransmitter.ClientId, TestTransmitter.ClientSecret, options);
        Assert.Equal(TestTransmitter.DefaultAccessToken, await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task TokenProviderReportsRejectedCredentials()
    {
        using ClientCredentialsSsfTransmitterTokenProvider provider = new(_http, new Uri(_transmitter.TokenUri), TestTransmitter.ClientId, "wrong");
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => provider.GetAccessTokenAsync().AsTask());
    }

    [Fact]
    public async Task ManagesStream()
    {
        Assert.Empty(await _streamClient.GetStreamsAsync());

        SsfStreamConfiguration created = await _streamClient.CreateStreamAsync(
            SsfStreamConfiguration.Push(PushEndpoint, "Bearer secret", ["CaepSessionRevoked"], "test"));
        Assert.False(string.IsNullOrWhiteSpace(created.StreamId));
        Assert.Equal(_transmitter.Issuer, created.Issuer);
        Assert.Equal([TestTransmitter.ClientId + "/" + created.StreamId], created.Audience);
        Assert.Equal(SsfDeliveryMethod.Push.Uri(), created.DeliveryMethod);
        Assert.Equal(PushEndpoint, created.DeliveryEndpointUrl);
        Assert.Equal([SsfEventTypes.CaepSessionRevoked], created.EventsRequested);
        Assert.Equal("test", created.Description);

        Assert.Equal([created.StreamId], (await _streamClient.GetStreamsAsync()).Select(s => s.StreamId));
        Assert.Equal(PushEndpoint, (await _streamClient.GetStreamAsync(created.StreamId!)).DeliveryEndpointUrl);

        SsfStreamConfiguration updated = await _streamClient.UpdateStreamAsync(created.StreamId!,
            new JsonObject { ["events_requested"] = new JsonArray(SsfEventTypes.CaepCredentialChange) });
        Assert.Equal([SsfEventTypes.CaepCredentialChange], updated.EventsRequested);
        Assert.Equal(PushEndpoint, updated.DeliveryEndpointUrl);

        SsfStreamConfiguration replaced = await _streamClient.ReplaceStreamAsync(created.StreamId!,
            SsfStreamConfiguration.Push(PushEndpoint, null, ["CaepSessionRevoked"], "replaced"));
        Assert.Equal("replaced", replaced.Description);
        Assert.Equal(created.StreamId, replaced.StreamId);

        Assert.Equal(SsfStreamStatus.Enabled, (await _streamClient.GetStatusAsync(created.StreamId!)).Status);
        Assert.Equal(SsfStreamStatus.Paused, (await _streamClient.UpdateStatusAsync(created.StreamId!, SsfStreamStatus.Paused, "maintenance")).Status);

        await _streamClient.RequestVerificationAsync(created.StreamId!, "state-1");
        Assert.Equal(["state-1"], _transmitter.VerificationRequests);

        JsonObject subject = new() { ["format"] = "email", ["email"] = "alice@example.com" };
        await _streamClient.AddSubjectAsync(created.StreamId!, subject, true);
        await _streamClient.RemoveSubjectAsync(created.StreamId!, subject);
        Assert.Equal(subject.ToJsonString(), Assert.Single(_transmitter.AddedSubjects).ToJsonString());
        Assert.Equal(subject.ToJsonString(), Assert.Single(_transmitter.RemovedSubjects).ToJsonString());

        await _streamClient.DeleteStreamAsync(created.StreamId!);
        Assert.Empty(await _streamClient.GetStreamsAsync());
    }

    [Fact]
    public async Task ReportsStatusOfFailedCalls()
    {
        SsfStreamException ex = await Assert.ThrowsAsync<SsfStreamException>(() => StreamClient("wrong-token").GetStreamsAsync());
        Assert.Equal(401, ex.StatusCode);
        ex = await Assert.ThrowsAsync<SsfStreamException>(() => _streamClient.GetStreamAsync("unknown"));
        Assert.Equal(404, ex.StatusCode);
        ex = await Assert.ThrowsAsync<SsfStreamException>(() => _streamClient.UpdateStatusAsync("unknown", SsfStreamStatus.Paused, null));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task ReportsUnavailableTransmitterWithoutStatus()
    {
        _transmitter.Available = false;
        // the token endpoint is down as well: the token provider reports that, the client wraps it
        SsfStreamException ex = await Assert.ThrowsAsync<SsfStreamException>(() => _streamClient.GetStreamsAsync());
        Assert.Equal(0, ex.StatusCode);
        Assert.IsType<SsfTransmitterUnavailableException>(ex.InnerException);
        using TestTransmitter noMetadata = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.None);
        using HttpClient http = noMetadata.CreateHttpClient();
        SsfStreamClient withoutMetadata = new(http, SsfTransmitterTokenProviders.None, new SsfTransmitterMetadataResolver(noMetadata.Issuer, null, http));
        ex = await Assert.ThrowsAsync<SsfStreamException>(() => withoutMetadata.GetStreamsAsync());
        Assert.Equal(0, ex.StatusCode);
    }

    [Fact]
    public async Task RefusesStreamCreatedWithAnotherIssuer()
    {
        _transmitter.StreamIssuer = "https://other.example";
        SsfStreamIssuerMismatchException ex = await Assert.ThrowsAsync<SsfStreamIssuerMismatchException>(
            () => _streamClient.CreateStreamAsync(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null)));
        Assert.Equal(_transmitter.Streams[0]["stream_id"]!.GetValue<string>(), ex.Stream.StreamId);
        Assert.Contains("https://other.example", ex.Message);
    }

    [Fact]
    public async Task RegistrarDeletesStreamCreatedWithAnotherIssuerAndGivesUp()
    {
        _transmitter.StreamIssuer = "https://other.example";
        using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
        registrar.Start();
        try
        {
            await Eventually.UntilAsync(() => registrar.State == SsfStreamRegistrarState.Failed && _transmitter.Streams.Count == 0);
            await Eventually.DuringAsync(() => _transmitter.Streams.Count == 0, TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            await registrar.StopAsync();
        }
        Assert.Null(_receiverStream.StreamId);
        Assert.Contains("https://other.example", registrar.LastError);
    }

    [Fact]
    public async Task ObtainsNewAccessTokenWhenTheTransmitterRejectsTheCurrentOne()
    {
        Assert.Empty(await _streamClient.GetStreamsAsync());
        int tokenRequests = _transmitter.TokenRequests;

        _transmitter.ExpireAccessTokens();

        Assert.Empty(await _streamClient.GetStreamsAsync());
        Assert.Equal(tokenRequests + 1, _transmitter.TokenRequests);
    }

    [Fact]
    public async Task RegistrarCreatesStream()
    {
        SsfStreamConfiguration stream = await Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null)).RegisterAsync();
        Assert.Single(_transmitter.Streams);
        Assert.Equal(_transmitter.PollUri, stream.DeliveryEndpointUrl!.ToString());
        Assert.Equal(stream.StreamId, _receiverStream.StreamId);
        Assert.Equal([TestTransmitter.ClientId + "/" + stream.StreamId], _receiverStream.Audience);
    }

    [Fact]
    public async Task RegistrarTellsItsListenersOnceTheStreamIsRegistered()
    {
        List<SsfStreamConfiguration> registered = [];
        using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.AddListener(_ => throw new InvalidOperationException("a failing listener does not keep the others from being told"));
        registrar.AddListener(registered.Add);
        SsfStreamConfiguration stream = await registrar.RegisterAsync();
        Assert.Equal([stream], registered);
        Assert.Equal(stream.StreamId, _receiverStream.StreamId);
        Assert.Equal(SsfStreamRegistrarState.Registered, registrar.State);
    }

    [Fact]
    public async Task RegistrarReusesExistingStream()
    {
        SsfStreamConfiguration desired = SsfStreamConfiguration.Push(PushEndpoint, null, ["CaepSessionRevoked", "CaepCredentialChange"], null);
        string? streamId = (await Registrar(desired).RegisterAsync()).StreamId;
        Assert.Equal(streamId, (await Registrar(desired).RegisterAsync()).StreamId);
        Assert.Single(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrarUpdatesRequestedEventsOfExistingStream()
    {
        string? streamId = (await Registrar(SsfStreamConfiguration.Push(PushEndpoint, null, ["CaepSessionRevoked"], null)).RegisterAsync()).StreamId;
        SsfStreamConfiguration stream = await Registrar(
            SsfStreamConfiguration.Push(PushEndpoint, null, ["CaepSessionRevoked", "CaepCredentialChange"], null)).RegisterAsync();
        Assert.Equal(streamId, stream.StreamId);
        Assert.Equal(new[] { SsfEventTypes.CaepSessionRevoked, SsfEventTypes.CaepCredentialChange }.Order(), stream.EventsRequested.Order());
    }

    [Fact]
    public async Task RegistrarChangesDeliveryOfTheOnlyStreamTheTransmitterAllows()
    {
        string? streamId = (await Registrar(SsfStreamConfiguration.Push(PushEndpoint, null, ["CaepSessionRevoked"], null)).RegisterAsync()).StreamId;
        SsfStreamConfiguration stream = await Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null)).RegisterAsync();
        Assert.Equal(streamId, stream.StreamId);
        Assert.Equal(SsfDeliveryMethod.Poll.Uri(), stream.DeliveryMethod);
        Assert.Equal(_transmitter.PollUri, stream.DeliveryEndpointUrl!.ToString());
        Assert.Single(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrarLooksUpStreamCreatedAtTheTransmitter()
    {
        JsonObject existing = _transmitter.AddStream(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null).ToJsonObject());
        string streamId = existing["stream_id"]!.GetValue<string>();
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForExistingStream(_streamClient, _receiverStream, streamId);
        Assert.Equal(_transmitter.PollUri, (await registrar.RegisterAsync()).DeliveryEndpointUrl!.ToString());
        Assert.Equal(streamId, _receiverStream.StreamId);
    }

    [Fact]
    public async Task RegistrarRetriesInTheBackgroundUntilTheTransmitterAnswers()
    {
        _transmitter.Available = false;
        using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
        registrar.Start();
        try
        {
            // the first attempt fails and is recorded; the registrar keeps trying without a stream
            await Eventually.UntilAsync(() => registrar.LastError is not null);
            Assert.Null(_receiverStream.StreamId);
            Assert.Equal(SsfStreamRegistrarState.Registering, registrar.State);
            await Eventually.DuringAsync(() => _receiverStream.StreamId is null && registrar.State == SsfStreamRegistrarState.Registering,
                TimeSpan.FromMilliseconds(200));
            _transmitter.Available = true;
            await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        }
        finally
        {
            await registrar.StopAsync();
        }
        Assert.Single(_transmitter.Streams);
        Assert.Null(registrar.LastError);
    }

    [Fact]
    public async Task RegistrarStoppedRightAfterStartingIsSafe()
    {
        // StopAsync may dispose the cancellation source before the background task has started;
        // the task must not fault on that (seen on a slow CI runner)
        _transmitter.Available = false;
        for (int i = 0; i < 25; i++)
        {
            using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
            registrar.Start();
            await registrar.StopAsync();
        }
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrarDeletesManagedStreamOnShutdownIfAsked()
    {
        using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
    }

    private SsfStreamRegistrar Registrar(SsfStreamConfiguration desired)
    {
        return SsfStreamRegistrar.ForManagedStream(_streamClient, _receiverStream, desired);
    }
}
