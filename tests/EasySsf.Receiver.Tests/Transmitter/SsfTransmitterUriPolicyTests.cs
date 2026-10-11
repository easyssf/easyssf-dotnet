using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

namespace EasySsf.Receiver.Tests.Transmitter;

public class SsfTransmitterUriPolicyTests
{
    private readonly SsfTransmitterUriPolicy _policy = SsfTransmitterUriPolicy.Default;

    [Fact]
    public void AcceptsHttpsIssuerWithPath()
    {
        Assert.Equal(new Uri("https://idp.example/realms/demo"), _policy.CheckIssuer("https://idp.example/realms/demo"));
        _policy.CheckIssuer("HTTPS://idp.example:8443");
    }

    [Fact]
    public void RejectsHttpIssuerExceptOnLoopback()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("http://idp.example/realms/demo"));
        Assert.Contains("must use https", ex.Message);
        _policy.CheckIssuer("http://localhost:8080/realms/demo");
        _policy.CheckIssuer("http://127.0.0.1:8080/realms/demo");
        _policy.CheckIssuer("http://[::1]:8080/realms/demo");
        _policy.CheckIssuer("http://idp.localhost/realms/demo");
    }

    [Fact]
    public void InsecurePolicyAcceptsHttpAnywhere()
    {
        SsfTransmitterUriPolicy.Insecure.CheckIssuer("http://idp.example/realms/demo");
        Assert.Throws<ArgumentException>(() => SsfTransmitterUriPolicy.Insecure.CheckIssuer("file:///etc/passwd"));
    }

    [Fact]
    public void RejectsIssuerWithQueryFragmentOrWithoutHost()
    {
        Assert.Contains("query or fragment", Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("https://idp.example/x?y=z")).Message);
        Assert.Contains("query or fragment", Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("https://idp.example/x#y")).Message);
        Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("/realms/demo"));
        Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("https:///x"));
        Assert.Throws<ArgumentException>(() => _policy.CheckIssuer("not a uri"));
    }

    [Fact]
    public void EndpointsFollowTheSameRulesButMayHaveAQuery()
    {
        _policy.CheckEndpoint(new Uri("https://idp.example/streams?x=1"), "endpoint");
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _policy.CheckEndpoint(new Uri("http://idp.example/jwks"), "jwks_uri"));
        Assert.Contains("jwks_uri", ex.Message);
        Assert.Throws<ArgumentException>(() => _policy.CheckEndpoint(new Uri("https://idp.example/jwks#x"), "jwks_uri"));
        Assert.Throws<ArgumentException>(() => _policy.CheckEndpoint(null, "jwks_uri"));
        Assert.Throws<ArgumentException>(() => _policy.CheckEndpoint(new Uri("/jwks", UriKind.Relative), "jwks_uri"));
    }

    [Fact]
    public void ResolverAppliesThePolicyToTheIssuerAndTheMetadataUrl()
    {
        using TestTransmitter transmitter = new();
        using HttpClient http = transmitter.CreateHttpClient();
        Assert.Throws<ArgumentException>(() => new SsfTransmitterMetadataResolver("http://idp.example", null, http));
        Assert.Throws<ArgumentException>(() => new SsfTransmitterMetadataResolver("https://idp.example",
            new Uri("http://idp.example/.well-known/ssf-configuration"), http));
        using SsfTransmitterMetadataResolver insecure = new("http://idp.example", null, http, SsfTransmitterUriPolicy.Insecure);
        Assert.Equal("http://idp.example", insecure.Issuer);
    }

    [Fact]
    public void KnowsInsecureAndLoopbackUris()
    {
        Assert.True(SsfTransmitterUriPolicy.IsInsecure(new Uri("http://idp.example")));
        Assert.False(SsfTransmitterUriPolicy.IsInsecure(new Uri("https://idp.example")));
        Assert.False(SsfTransmitterUriPolicy.IsInsecure(null));
        Assert.True(SsfTransmitterUriPolicy.IsLoopback(new Uri("http://localhost")));
        Assert.True(SsfTransmitterUriPolicy.IsLoopback(new Uri("http://app.localhost")));
        Assert.False(SsfTransmitterUriPolicy.IsLoopback(new Uri("http://idp.example")));
    }
}
