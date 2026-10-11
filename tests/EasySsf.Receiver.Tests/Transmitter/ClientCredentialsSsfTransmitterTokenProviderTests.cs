using System.Net;

using EasySsf.Receiver.Transmitter;

namespace EasySsf.Receiver.Tests.Transmitter;

public sealed class ClientCredentialsSsfTransmitterTokenProviderTests : IDisposable
{
    private static string TokenResponse(int number)
    {
        return $$"""{"access_token":"t-{{number}}","token_type":"Bearer","expires_in":300}""";
    }

    private readonly ScriptedHttpMessageHandler _http = new();

    private readonly HttpClient _client;

    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-10-03T10:00:00Z"));

    private readonly ClientCredentialsSsfTransmitterTokenProvider _provider;

    public ClientCredentialsSsfTransmitterTokenProviderTests()
    {
        _client = new HttpClient(_http);
        _provider = new ClientCredentialsSsfTransmitterTokenProvider(_client, new Uri("https://idp.example/token"), "receiver", "secret",
            new SsfClientCredentialsOptions { TimeProvider = _clock });
    }

    public void Dispose()
    {
        _provider.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task SendsTheGrantWithScopesAndAdditionalParameters()
    {
        _provider.Options.Scopes.Add("ssf.read");
        _provider.Options.Scopes.Add("ssf.manage");
        _provider.Options.AdditionalParameters["audience"] = "https://tr.example";
        _provider.Options.AdditionalParameters["grant_type"] = "ignored";
        _http.Respond(HttpStatusCode.OK, TokenResponse(1));
        Assert.Equal("t-1", await _provider.GetAccessTokenAsync());
        string body = _http.Requests[0].Body!;
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("scope=ssf.read+ssf.manage", body);
        Assert.Contains("audience=https%3A%2F%2Ftr.example", body);
        Assert.DoesNotContain("grant_type=ignored", body);
        Assert.Equal("Basic", _http.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task RenewsTheTokenAheadOfItsExpiryByTheSafetyWindow()
    {
        _http.Respond(HttpStatusCode.OK, TokenResponse(1)).Respond(HttpStatusCode.OK, TokenResponse(2));
        Assert.Equal("t-1", await _provider.GetAccessTokenAsync());
        // 300s lifetime, 30s default window: renewed after 270s
        _clock.Advance(TimeSpan.FromSeconds(269));
        Assert.Equal("t-1", await _provider.GetAccessTokenAsync());
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("t-2", await _provider.GetAccessTokenAsync());
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task SafetyWindowIsConfigurableButAtMostAQuarterOfTheLifetime()
    {
        _provider.Options.ExpirySafetyWindow = TimeSpan.FromSeconds(100);
        _http.Respond(HttpStatusCode.OK, TokenResponse(1)).Respond(HttpStatusCode.OK, TokenResponse(2));
        await _provider.GetAccessTokenAsync();
        // a quarter of 300s is 75s: renewed after 225s, not after 200s
        _clock.Advance(TimeSpan.FromSeconds(224));
        Assert.Equal("t-1", await _provider.GetAccessTokenAsync());
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("t-2", await _provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task InvalidateMakesTheNextCallRequestAToken()
    {
        _http.Respond(HttpStatusCode.OK, TokenResponse(1)).Respond(HttpStatusCode.OK, TokenResponse(2));
        Assert.Equal("t-1", await _provider.GetAccessTokenAsync());
        _provider.Invalidate();
        Assert.Equal("t-2", await _provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task ReportsATokenEndpointThatDoesNotAnswerWithAToken()
    {
        _http.Respond(HttpStatusCode.OK, "{\"token_type\":\"Bearer\"}");
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => _provider.GetAccessTokenAsync().AsTask());
        _http.Respond(HttpStatusCode.InternalServerError, "");
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => _provider.GetAccessTokenAsync().AsTask());
    }
}
