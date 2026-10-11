using EasySsf.Core.Metadata;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

namespace EasySsf.Receiver.Tests.Transmitter;

public class SsfTransmitterMetadataResolverTests
{
    [Fact]
    public void InsertsWellKnownPathBetweenHostAndIssuerPath()
    {
        Assert.Equal([new Uri("https://tr.example.com/.well-known/ssf-configuration/issuer1"),
            new Uri("https://tr.example.com/issuer1/.well-known/ssf-configuration")],
            SsfTransmitterMetadataResolver.DeriveMetadataUris("https://tr.example.com/issuer1"));
    }

    [Fact]
    public void IssuerWithoutPathHasSingleLocation()
    {
        Uri expected = new("https://tr.example.com/.well-known/ssf-configuration");
        Assert.Equal([expected], SsfTransmitterMetadataResolver.DeriveMetadataUris("https://tr.example.com"));
        Assert.Equal([expected], SsfTransmitterMetadataResolver.DeriveMetadataUris("https://tr.example.com/"));
    }

    [Fact]
    public void RejectsRelativeIssuer()
    {
        Assert.Throws<ArgumentException>(() => SsfTransmitterMetadataResolver.DeriveMetadataUris("/realms/test"));
    }

    [Fact]
    public async Task ResolvesMetadataFromSsfLocation()
    {
        using TestTransmitter transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf);
        using HttpClient http = transmitter.CreateHttpClient();
        using SsfTransmitterMetadataResolver resolver = new(transmitter.Issuer, null, http);
        Assert.Null(resolver.ResolvedMetadata);
        SsfTransmitterMetadata metadata = await resolver.ResolveAsync();
        Assert.Equal(transmitter.Issuer, metadata.Issuer);
        Assert.Equal(transmitter.JwksUri, metadata.JwksUri!.ToString());
        Assert.Same(metadata, resolver.ResolvedMetadata);
        int requests = transmitter.Requests;
        Assert.Same(metadata, await resolver.ResolveAsync());
        Assert.Equal(requests, transmitter.Requests);
    }

    [Fact]
    public async Task FallsBackToLocationAppendedToIssuer()
    {
        using TestTransmitter transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.OidcStyle);
        using HttpClient http = transmitter.CreateHttpClient();
        using SsfTransmitterMetadataResolver resolver = new(transmitter.Issuer, null, http);
        Assert.Equal(transmitter.JwksUri, (await resolver.ResolveAsync()).JwksUri!.ToString());
    }

    [Fact]
    public async Task UnresolvableMetadataIsReportedAsUnavailableAndRetried()
    {
        using TestTransmitter transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.None);
        using HttpClient http = transmitter.CreateHttpClient();
        using SsfTransmitterMetadataResolver resolver = new(transmitter.Issuer, null, http);
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => resolver.ResolveAsync());
        await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => resolver.ResolveAsync());
        Assert.Equal(4, transmitter.Requests);
        transmitter.PublishMetadata(TestTransmitter.MetadataLocation.Ssf);
        Assert.NotNull(await resolver.ResolveAsync());
    }

    [Fact]
    public async Task RejectsMetadataOfAnotherIssuer()
    {
        using TestTransmitter transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf);
        using HttpClient http = transmitter.CreateHttpClient();
        Uri metadataUri = SsfTransmitterMetadataResolver.DeriveMetadataUris(transmitter.Issuer)[0];
        using SsfTransmitterMetadataResolver resolver = new("https://other.example", metadataUri, http);
        SsfTransmitterUnavailableException ex = await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => resolver.ResolveAsync());
        Assert.Contains("does not match the configured transmitter issuer", ex.InnerException!.Message);
    }

    [Fact]
    public async Task RejectsMetadataWithInsecureEndpoints()
    {
        using TestTransmitter transmitter = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.None);
        transmitter.PublishDocument("/.well-known/ssf-configuration/realms/test",
            $$$"""{"issuer":"{{{transmitter.Issuer}}}","jwks_uri":"http://keys.example/jwks"}""");
        using HttpClient http = transmitter.CreateHttpClient();
        using SsfTransmitterMetadataResolver resolver = new(transmitter.Issuer, null, http);
        SsfTransmitterUnavailableException ex = await Assert.ThrowsAsync<SsfTransmitterUnavailableException>(() => resolver.ResolveAsync());
        Assert.Contains("jwks_uri", ex.InnerException!.Message);
        using SsfTransmitterMetadataResolver insecure = new(transmitter.Issuer, null, http, SsfTransmitterUriPolicy.Insecure);
        Assert.Equal(new Uri("http://keys.example/jwks"), (await insecure.ResolveAsync()).JwksUri);
    }
}
