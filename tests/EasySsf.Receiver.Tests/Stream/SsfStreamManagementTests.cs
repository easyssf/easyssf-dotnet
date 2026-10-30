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

    /// <summary>how long StopAsync() waits for a registration that does not end, shortened</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(200);

    private readonly List<HttpClient> _clients = [];

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
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
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
    public async Task RefusesExistingStreamsWithAnotherIssuer()
    {
        _transmitter.StreamIssuer = "https://other.example";
        JsonObject existing = _transmitter.AddStream(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null).ToJsonObject());
        string streamId = existing["stream_id"]!.GetValue<string>();
        await Assert.ThrowsAsync<SsfStreamIssuerMismatchException>(() => _streamClient.GetStreamAsync(streamId));
        await Assert.ThrowsAsync<SsfStreamIssuerMismatchException>(() => _streamClient.GetStreamsAsync());
        await Assert.ThrowsAsync<SsfStreamIssuerMismatchException>(
            () => _streamClient.UpdateStreamAsync(streamId, new JsonObject { ["description"] = "x" }));
        await Assert.ThrowsAsync<SsfStreamIssuerMismatchException>(
            () => _streamClient.ReplaceStreamAsync(streamId, SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null)));
    }

    [Fact]
    public async Task RegistrarGivesUpOnExistingStreamWithAnotherIssuerWithoutDeletingIt()
    {
        _transmitter.StreamIssuer = "https://other.example";
        JsonObject existing = _transmitter.AddStream(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null).ToJsonObject());
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForExistingStream(_streamClient, _receiverStream,
            existing["stream_id"]!.GetValue<string>());
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
        registrar.Start();
        try
        {
            await Eventually.UntilAsync(() => registrar.State == SsfStreamRegistrarState.Failed);
        }
        finally
        {
            await registrar.StopAsync();
        }
        Assert.Contains("https://other.example", registrar.LastError);
        Assert.Null(_receiverStream.StreamId);
        // the stream is not this registrar's to delete
        Assert.Single(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrarStartIsIdempotentAndStopEndsTheRegistration()
    {
        _transmitter.Available = false;
        int calls = 0;
        int inFlight = 0;
        int maxInFlight = 0;
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            Interlocked.Increment(ref calls);
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            try
            {
                // long enough for the calls of a second registration to overlap
                await Task.Delay(30);
                return await send();
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(20);
        registrar.Start();
        registrar.Start();
        Assert.True(registrar.IsRunning);
        await Eventually.UntilAsync(() => Volatile.Read(ref calls) >= 3);
        // the retries all come from the one registration
        Assert.Equal(1, Volatile.Read(ref maxInFlight));

        await registrar.StopAsync();
        await registrar.StopAsync();
        Assert.False(registrar.IsRunning);
        int callsAtStop = Volatile.Read(ref calls);
        await Eventually.DuringAsync(() => Volatile.Read(ref calls) == callsAtStop, TimeSpan.FromMilliseconds(200));
        Assert.Null(_receiverStream.StreamId);
    }

    [Fact]
    public async Task StopDeletesOnlyAStreamPublishedSinceTheLastStop()
    {
        int deletes = 0;
        SsfStreamClient client = InterceptedStreamClient((request, send) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref deletes);
            }
            return send();
        });
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(20);
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        await registrar.StopAsync();
        Assert.Equal(1, Volatile.Read(ref deletes));

        // the receiver still knows the deleted stream: neither a second stop nor the stop of a
        // registration that never published deletes it again
        await registrar.StopAsync();
        _transmitter.Available = false;
        registrar.Start();
        await registrar.StopAsync();
        Assert.Equal(1, Volatile.Read(ref deletes));
        Assert.NotNull(_receiverStream.StreamId);
    }

    [Fact]
    public async Task RegistrationThatCompletesAfterStopPublishesNothing()
    {
        TaskCompletionSource streamsRequested = NewSignal();
        TaskCompletionSource release = NewSignal();
        // a handler that does not observe the cancellation of the stop and completes the
        // registration afterwards
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            if (IsStreams(request) && request.Method == HttpMethod.Get)
            {
                streamsRequested.TrySetResult();
                await release.Task;
            }
            return await send();
        });
        List<SsfStreamConfiguration> registered = [];
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.AddListener(registered.Add);
        registrar.Start();
        await streamsRequested.Task.WaitAsync(Eventually.Default);

        Task stopping = registrar.StopAsync();
        // StopAsync() waits for the registration, which completes only once released
        await Eventually.DuringAsync(() => !stopping.IsCompleted, TimeSpan.FromMilliseconds(200));
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        // the stream created on the way is deleted, as it would have been on shutdown
        await Eventually.UntilAsync(() => _transmitter.Streams.Count == 0);
        Assert.Empty(registered);
        Assert.Null(_receiverStream.StreamId);
        Assert.NotEqual(SsfStreamRegistrarState.Registered, registrar.State);
    }

    [Fact]
    public async Task StopWaitsForAPublicationInProgressAndDeletesTheStreamItPublished()
    {
        TaskCompletionSource publishing = NewSignal();
        ManualResetEventSlim release = new();
        List<SsfStreamConfiguration> registered = [];
        using SsfStreamRegistrar registrar = Registrar(SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.AddListener(stream =>
        {
            // the registration passed its check and is publishing when StopAsync() is called
            registered.Add(stream);
            publishing.TrySetResult();
            release.Wait();
        });
        registrar.Start();
        await publishing.Task.WaitAsync(Eventually.Default);

        Task stopping = Task.Run(() => registrar.StopAsync());
        await Eventually.DuringAsync(() => !stopping.IsCompleted, TimeSpan.FromMilliseconds(200));
        Assert.Single(_transmitter.Streams);
        release.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        // the publication completed before StopAsync() went on, so the stream it published is the
        // one StopAsync() deleted
        Assert.Single(registered);
        Assert.Equal(registered[0].StreamId, _receiverStream.StreamId);
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrationThatOutlivesItsStopLeavesTheStreamToTheRegistrationThatFollowed()
    {
        TaskCompletionSource created = NewSignal();
        TaskCompletionSource release = NewSignal();
        // the transmitter created the stream, but the handler does not return, and does not observe
        // the cancellation of the stop either
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            HttpResponseMessage response = await send();
            if (IsStreams(request) && request.Method == HttpMethod.Post)
            {
                created.TrySetResult();
                await release.Task;
            }
            return response;
        });
        List<SsfStreamConfiguration> registered = [];
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.StopTimeout = StopTimeout;
        registrar.AddListener(registered.Add);
        registrar.Start();
        await created.Task.WaitAsync(Eventually.Default);
        string streamId = _transmitter.Streams[0]["stream_id"]!.GetValue<string>();

        // StopAsync() gives the first registration up after its timeout, Start() begins a second
        // one, which finds the stream the first created and adopts it
        long stopping = TimeProvider.System.GetTimestamp();
        await registrar.StopAsync();
        Assert.True(TimeProvider.System.GetElapsedTime(stopping) >= StopTimeout);
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        Assert.Equal(streamId, _receiverStream.StreamId);

        // the first registration returns at last: it neither publishes nor deletes the stream the
        // second one uses
        release.SetResult();
        await Eventually.DuringAsync(() => _transmitter.Streams.Count == 1, TimeSpan.FromMilliseconds(300));
        Assert.Single(registered);
        Assert.Equal(SsfStreamRegistrarState.Registered, registrar.State);

        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task RegistrationThatOutlivesItsStopDeletesTheStreamItCreatedAfterTheNextOnePublished()
    {
        _transmitter.SeveralStreamsAllowed = true;
        TaskCompletionSource discovered = NewSignal();
        TaskCompletionSource release = NewSignal();
        int creates = 0;
        // the first registration stalls after it looked for streams and found none, and does not
        // observe the cancellation of the stop either
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            HttpResponseMessage response = await send();
            if (IsStreams(request) && request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref creates);
            }
            if (IsStreams(request) && request.Method == HttpMethod.Get && discovered.TrySetResult())
            {
                await release.Task;
            }
            return response;
        });
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.StopTimeout = StopTimeout;
        registrar.Start();
        await discovered.Task.WaitAsync(Eventually.Default);

        // StopAsync() gives the first registration up, the second finds no stream and creates one
        await registrar.StopAsync();
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        string published = _receiverStream.StreamId!;

        // the first registration goes on to create a stream of its own, which nobody uses
        release.SetResult();
        await Eventually.UntilAsync(() => Volatile.Read(ref creates) == 2);
        await Eventually.UntilAsync(() => _transmitter.Streams.Count == 1);
        await Eventually.DuringAsync(() => _transmitter.Streams.Count == 1, TimeSpan.FromMilliseconds(300));
        Assert.Equal(published, _transmitter.Streams[0]["stream_id"]!.GetValue<string>());
        Assert.Equal(published, _receiverStream.StreamId);

        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task StreamAStoppedRegistrationLeavesIsAdoptedByTheNextOneAndNotMistakenForAnEarlierOne()
    {
        // a first registration published a stream and StopAsync() deleted it; the receiver still
        // remembers that stream, which must not pass for the one the next registration uses
        bool stallCreate = false;
        TaskCompletionSource created = NewSignal();
        TaskCompletionSource releaseCreate = NewSignal();
        bool stallDiscovery = false;
        TaskCompletionSource discovered = NewSignal();
        TaskCompletionSource releaseDiscovery = NewSignal();
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            HttpResponseMessage response = await send();
            if (IsStreams(request) && request.Method == HttpMethod.Post && Interlocked.Exchange(ref stallCreate, false))
            {
                created.TrySetResult();
                await releaseCreate.Task;
            }
            if (IsStreams(request) && request.Method == HttpMethod.Get && Interlocked.Exchange(ref stallDiscovery, false))
            {
                discovered.TrySetResult();
                await releaseDiscovery.Task;
            }
            return response;
        });
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.StopTimeout = StopTimeout;
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        string earlier = _receiverStream.StreamId!;
        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
        Assert.Equal(earlier, _receiverStream.StreamId);

        // the second registration creates a stream and stalls; StopAsync() gives it up and the
        // third finds that stream, and stalls before it publishes it
        Volatile.Write(ref stallCreate, true);
        registrar.Start();
        await created.Task.WaitAsync(Eventually.Default);
        string streamId = _transmitter.Streams[0]["stream_id"]!.GetValue<string>();
        await registrar.StopAsync();
        Volatile.Write(ref stallDiscovery, true);
        registrar.Start();
        await discovered.Task.WaitAsync(Eventually.Default);

        // the second returns now: the third has not published, so the stream is left to it, not
        // deleted for differing from the stream of the first
        releaseCreate.SetResult();
        await Eventually.DuringAsync(() => _transmitter.Streams.Count == 1, TimeSpan.FromMilliseconds(300));
        releaseDiscovery.SetResult();
        await Eventually.UntilAsync(() => _receiverStream.StreamId == streamId);
        Assert.Single(_transmitter.Streams);
        Assert.Equal(SsfStreamRegistrarState.Registered, registrar.State);

        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task StreamAStoppedRegistrationCreatesAfterTheNextOneLookedIsDeletedOnceTheNextOnePublished()
    {
        _transmitter.SeveralStreamsAllowed = true;
        // the first registration stalls after it looked and found no stream; the second finds none
        // either, creates a stream and stalls before it publishes; the first then creates a stream
        // nobody will adopt
        TaskCompletionSource discovered = NewSignal();
        TaskCompletionSource releaseDiscovery = NewSignal();
        bool stallCreate = false;
        TaskCompletionSource created = NewSignal();
        TaskCompletionSource releaseCreate = NewSignal();
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            HttpResponseMessage response = await send();
            if (IsStreams(request) && request.Method == HttpMethod.Get && discovered.TrySetResult())
            {
                await releaseDiscovery.Task;
            }
            if (IsStreams(request) && request.Method == HttpMethod.Post && Interlocked.Exchange(ref stallCreate, false))
            {
                created.TrySetResult();
                await releaseCreate.Task;
            }
            return response;
        });
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.StopTimeout = StopTimeout;
        registrar.Start();
        await discovered.Task.WaitAsync(Eventually.Default);
        await registrar.StopAsync();
        Volatile.Write(ref stallCreate, true);
        registrar.Start();
        await created.Task.WaitAsync(Eventually.Default);
        string published = _transmitter.Streams[0]["stream_id"]!.GetValue<string>();

        // the first creates its stream while the second has not published
        releaseDiscovery.SetResult();
        await Eventually.UntilAsync(() => _transmitter.Streams.Count == 2);
        await Eventually.DuringAsync(() => _transmitter.Streams.Count == 2, TimeSpan.FromMilliseconds(300));

        // the second publishes its own stream: the other one is an orphan and goes
        releaseCreate.SetResult();
        await Eventually.UntilAsync(() => _transmitter.Streams.Count == 1);
        Assert.Equal(published, _transmitter.Streams[0]["stream_id"]!.GetValue<string>());
        Assert.Equal(published, _receiverStream.StreamId);

        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task StreamWhoseDeletionIsUnderWayIsNotAdoptedByTheNextRegistration()
    {
        // the first registration stalls after it looked and found no stream; the second creates a
        // stream and publishes it; the first then creates an orphan and deletes it, which the
        // transmitter takes its time for; the registrar is restarted meanwhile
        _transmitter.SeveralStreamsAllowed = true;
        TaskCompletionSource discovered = NewSignal();
        TaskCompletionSource releaseDiscovery = NewSignal();
        string? second = null;
        string? orphan = null;
        TaskCompletionSource deleting = NewSignal();
        TaskCompletionSource releaseDelete = NewSignal();
        SsfStreamClient client = InterceptedStreamClient(async (request, send) =>
        {
            if (IsStreams(request) && request.Method == HttpMethod.Get && !discovered.Task.IsCompleted)
            {
                HttpResponseMessage response = await send();
                discovered.TrySetResult();
                await releaseDiscovery.Task;
                return response;
            }
            // the delete of the orphan, the only stream deleted while the second is in use
            string? inUse = Volatile.Read(ref second);
            if (request.Method == HttpMethod.Delete && inUse is not null && !request.RequestUri!.Query.Contains(inUse, StringComparison.Ordinal)
                    && !deleting.Task.IsCompleted)
            {
                Volatile.Write(ref orphan, Uri.UnescapeDataString(request.RequestUri.Query.Split("stream_id=")[1].Split('&')[0]));
                deleting.TrySetResult();
                await releaseDelete.Task;
            }
            return await send();
        });
        List<SsfStreamConfiguration> registered = [];
        using SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(client, _receiverStream,
            SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null));
        registrar.DeleteOnShutdown = true;
        registrar.StopTimeout = StopTimeout;
        registrar.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
        registrar.AddListener(stream =>
        {
            lock (registered)
            {
                registered.Add(stream);
            }
        });
        registrar.Start();
        await discovered.Task.WaitAsync(Eventually.Default);
        await registrar.StopAsync();
        registrar.Start();
        await Eventually.UntilAsync(() => _receiverStream.StreamId is not null);
        Volatile.Write(ref second, _receiverStream.StreamId);

        // the first creates its stream, an orphan, and gets stuck deleting it
        releaseDiscovery.SetResult();
        await deleting.Task.WaitAsync(Eventually.Default);
        Assert.Equal(2, _transmitter.Streams.Count);

        // restarted while the orphan still exists: the third registration finds it and must not
        // adopt it, it creates a stream of its own instead
        await registrar.StopAsync();
        Assert.Single(_transmitter.Streams);
        registrar.Start();
        await Eventually.UntilAsync(() =>
        {
            lock (registered)
            {
                return registered.Count == 2;
            }
        });
        string? third = _receiverStream.StreamId;
        Assert.NotEqual(Volatile.Read(ref orphan), third);
        Assert.NotEqual(second, third);

        releaseDelete.SetResult();
        await Eventually.UntilAsync(() => _transmitter.Streams.Count == 1);
        Assert.Equal(third, _transmitter.Streams[0]["stream_id"]!.GetValue<string>());

        await registrar.StopAsync();
        Assert.Empty(_transmitter.Streams);
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

    /// <summary>
    /// A stream client whose calls go through the given interceptor, which sends a call on with the
    /// function it is handed. The cancellation of the registrar's stop does not reach the
    /// transmitter, as with a handler that does not observe it.
    /// </summary>
    private SsfStreamClient InterceptedStreamClient(Func<HttpRequestMessage, Func<Task<HttpResponseMessage>>, Task<HttpResponseMessage>> intercept)
    {
        HttpClient http = new(new InterceptingHandler(_transmitter.Handler, intercept)) { Timeout = Timeout.InfiniteTimeSpan };
        _clients.Add(http);
        return new SsfStreamClient(http, SsfTransmitterTokenProviders.Fixed(TestTransmitter.DefaultAccessToken),
            new SsfTransmitterMetadataResolver(_transmitter.Issuer, null, _http));
    }

    private static bool IsStreams(HttpRequestMessage request)
    {
        return request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
            current = previous;
        }
    }

    private sealed class InterceptingHandler(HttpMessageHandler transmitter,
            Func<HttpRequestMessage, Func<Task<HttpResponseMessage>>, Task<HttpResponseMessage>> intercept) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _transmitter = new(transmitter, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return intercept(request, () => _transmitter.SendAsync(request, CancellationToken.None));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _transmitter.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
