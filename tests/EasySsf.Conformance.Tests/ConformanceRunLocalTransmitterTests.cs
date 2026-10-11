using System.Net;
using System.Text.Json.Nodes;

using EasySsf.Conformance.Receiver;
using EasySsf.Core.Event;
using EasySsf.Testing;
using EasySsf.Testing.Conformance.Receiver;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasySsf.Conformance.Tests;

/// <summary>
/// Runs the scenarios of the receiver under test against the test transmitter of the receiver
/// library, with poll delivery so that the transmitter does not have to reach this application.
/// </summary>
public sealed class ConformanceRunLocalTransmitterTests : IAsyncLifetime
{
    private readonly TestTransmitter _transmitter = new();

    private readonly HttpClient _http = new();

    private WebApplication _app = null!;

    private Uri _baseAddress = null!;

    public async Task InitializeAsync()
    {
        HttpClient transmitterClient = _transmitter.CreateHttpClient();
        _app = CtsApplication.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cts:Server:Url"] = "http://127.0.0.1:0",
                ["Cts:Transmitter:Issuer"] = _transmitter.Issuer,
                ["Cts:Delivery:Method"] = "Poll",
                ["Cts:Delivery:PollInterval"] = "00:00:00.200",
                ["Cts:Run:IdleTimeout"] = "00:00:02",
                ["Cts:Run:VerificationTimeout"] = "00:00:05",
                ["Cts:Auth:ClientId"] = TestTransmitter.ClientId,
                ["Cts:Auth:ClientSecret"] = TestTransmitter.ClientSecret,
                ["Logging:LogLevel:Default"] = "Warning",
            });
            builder.Services.AddSingleton(provider => new ConformanceRunner(
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConformanceReceiverSettings>>().Value, transmitterClient,
                provider.GetRequiredService<ILoggerFactory>()));
        });
        await _app.StartAsync();
        _baseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        _http.BaseAddress = _baseAddress;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _transmitter.Dispose();
    }

    [Fact]
    public async Task PlaysTheCaepInteropScenario()
    {
        JsonObject run = await StartAsync("caep-interop");
        Assert.Contains(run["status"]!.GetValue<string>(), new[] { "STARTING", "RUNNING" });

        string streamId = await AwaitStreamAndVerificationRequestAsync();
        string state = _transmitter.VerificationRequests[0];
        string verification = QueueVerificationEvent(streamId, state);
        await Eventually.UntilAsync(() => _transmitter.AcknowledgedSets.Contains(verification));

        string sessionRevoked = QueueSessionRevoked();
        await Eventually.UntilAsync(() => _transmitter.AcknowledgedSets.Contains(sessionRevoked));

        JsonObject finished = await AwaitStatusAsync("FINISHED");
        Assert.Empty(_transmitter.Streams);
        IReadOnlyList<string> log = Log(finished);
        Assert.Contains(log, entry => entry.Contains("Read stream status"));
        Assert.Contains(log, entry => entry.Contains("Verification event") && entry.Contains("accepted"));
        Assert.Contains(log, entry => entry.Contains("Deleted stream " + streamId));
        Assert.Empty(_transmitter.ReportedErrors);
    }

    [Fact]
    public async Task RejectsVerificationEventWithWrongStateAndRequestsVerificationAgain()
    {
        await StartAsync("caep-interop");
        string streamId = await AwaitStreamAndVerificationRequestAsync();
        string wrong = QueueVerificationEvent(streamId, "made-up");
        await Eventually.UntilAsync(() => _transmitter.ReportedErrors.ContainsKey(wrong));
        Assert.Equal("invalid_state", _transmitter.ReportedErrors[wrong]!["err"]!.GetValue<string>());
        await Eventually.UntilAsync(() => _transmitter.VerificationRequests.Count == 2);

        string right = QueueVerificationEvent(streamId, _transmitter.VerificationRequests[1]);
        await Eventually.UntilAsync(() => _transmitter.AcknowledgedSets.Contains(right));
        await AwaitStatusAsync("FINISHED");
        Assert.Empty(_transmitter.Streams);
    }

    [Fact]
    public async Task RejectsVerificationEventAboutAnotherStream()
    {
        await StartAsync("caep-interop");
        string streamId = await AwaitStreamAndVerificationRequestAsync();
        string wrong = QueueVerificationEvent("another-stream", _transmitter.VerificationRequests[0]);
        await Eventually.UntilAsync(() => _transmitter.ReportedErrors.ContainsKey(wrong));
        await Eventually.UntilAsync(() => _transmitter.VerificationRequests.Count == 2);
        string right = QueueVerificationEvent(streamId, _transmitter.VerificationRequests[1]);
        await Eventually.UntilAsync(() => _transmitter.AcknowledgedSets.Contains(right));
        await AwaitStatusAsync("FINISHED");
    }

    [Fact]
    public async Task RefusesStreamWithAnotherIssuer()
    {
        _transmitter.StreamIssuer = "https://other.example";
        await StartAsync("caep-interop");
        JsonObject run = await AwaitStatusAsync("REFUSED_STREAM");
        Assert.Empty(_transmitter.Streams);
        Assert.Empty(_transmitter.VerificationRequests);
        Assert.Contains(Log(run), entry => entry.Contains("Refusing the stream"));
    }

    [Fact]
    public async Task CreatesAndDeletesTheStream()
    {
        await StartAsync("create-delete");
        await AwaitStatusAsync("FINISHED");
        Assert.Empty(_transmitter.Streams);
        Assert.Empty(_transmitter.VerificationRequests);
    }

    [Fact]
    public async Task ManagesStreamAndStatusAndSubjects()
    {
        foreach (string scenario in new[] { "stream-management", "status-update", "remove-subject" })
        {
            _transmitter.Reset();
            await StartAsync(scenario);
            string streamId = await AwaitStreamAndVerificationRequestAsync();
            QueueVerificationEvent(streamId, _transmitter.VerificationRequests[0]);
            JsonObject run = await AwaitStatusAsync("FINISHED");
            Assert.Empty(_transmitter.Streams);
            Assert.Contains(Log(run), entry => entry.Contains("Replaced stream") || entry.Contains("Enabling the stream") || entry.Contains("Removed subject"));
        }
        Assert.Single(_transmitter.RemovedSubjects);
    }

    [Fact]
    public async Task PushEndpointAnswersWithoutRun()
    {
        // a run of another test may still be active, stop it first
        await _http.PostAsync("/cts/runs/current/stop", null);
        await Eventually.UntilAssertedAsync(async () => Assert.NotEqual("RUNNING", (await CurrentAsync())?["status"]?.GetValue<string>()));
        using HttpRequestMessage request = new(HttpMethod.Post, "/ssf/push") { Content = new StringContent("x", System.Text.Encoding.UTF8, "application/secevent+jwt") };
        using HttpResponseMessage response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No conformance run is active", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RejectsUnknownScenariosAndTellsTheUsage()
    {
        using HttpResponseMessage bad = await _http.PostAsync("/cts/runs?scenario=nope", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown scenario", await bad.Content.ReadAsStringAsync());
        Assert.Contains("POST /cts/runs/auto", await _http.GetStringAsync("/"));
    }

    private async Task<string> AwaitStreamAndVerificationRequestAsync()
    {
        await Eventually.UntilAsync(() => _transmitter.VerificationRequests.Count > 0, TimeSpan.FromSeconds(10));
        Assert.Single(_transmitter.Streams);
        return _transmitter.Streams[0]["stream_id"]!.GetValue<string>();
    }

    private string QueueVerificationEvent(string streamId, string state)
    {
        JsonObject claims = _transmitter.SetClaims("SsfStreamVerification", SsfSubjectIdentifiers.Opaque(streamId), new JsonObject { ["state"] = state });
        claims["aud"] = Audience();
        _transmitter.QueueSet(_transmitter.SignSet(claims));
        return claims["jti"]!.GetValue<string>();
    }

    private string QueueSessionRevoked()
    {
        JsonObject claims = _transmitter.SetClaims("CaepSessionRevoked", SsfSubjectIdentifiers.IssSub(_transmitter.Issuer, "alice"));
        claims["aud"] = Audience();
        _transmitter.QueueSet(_transmitter.SignSet(claims));
        return claims["jti"]!.GetValue<string>();
    }

    private JsonNode Audience()
    {
        return _transmitter.Streams[0]["aud"]!.DeepClone();
    }

    private async Task<JsonObject> StartAsync(string scenario)
    {
        using HttpResponseMessage response = await _http.PostAsync("/cts/runs?scenario=" + scenario, null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task<JsonObject?> CurrentAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync("/cts/runs/current");
        return response.IsSuccessStatusCode ? (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())! : null;
    }

    private async Task<JsonObject> AwaitStatusAsync(string status)
    {
        await Eventually.UntilAssertedAsync(async () => Assert.Equal(status, (await CurrentAsync())?["status"]?.GetValue<string>()), TimeSpan.FromSeconds(20));
        return (await CurrentAsync())!;
    }

    private static IReadOnlyList<string> Log(JsonObject run)
    {
        return run["log"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>()).ToList();
    }
}
