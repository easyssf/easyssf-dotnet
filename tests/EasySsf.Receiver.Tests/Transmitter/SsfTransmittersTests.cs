using System.Text;
using System.Text.Json.Nodes;

using EasySsf.Receiver.Event;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Transmitter;

/// <summary>
/// A receiver with two transmitters, assembled by hand: SETs are verified by the transmitter that
/// issued them, and each transmitter authenticates with its own header.
/// </summary>
public sealed class SsfTransmittersTests : IDisposable
{
    private readonly TestTransmitter _keycloak = new();

    private readonly TestTransmitter _okta = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.OidcStyle, "okta.example");

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        _disposables.ForEach(disposable => disposable.Dispose());
        _keycloak.Dispose();
        _okta.Dispose();
    }

    [Fact]
    public async Task VerifiesSetsOfEveryTransmitterByIssuer()
    {
        SsfTransmitters transmitters = Transmitters();
        Assert.Equal(_keycloak.Issuer, (await transmitters.Verifier.VerifyAsync(Set(_keycloak))).Iss);
        Assert.Equal(_okta.Issuer, (await transmitters.Verifier.VerifyAsync(Set(_okta))).Iss);
    }

    [Fact]
    public async Task RejectsSetsOfUnknownIssuersAndGarbage()
    {
        SsfTransmitters transmitters = Transmitters();
        using TestTransmitter stranger = TestTransmitter.WithMetadata(TestTransmitter.MetadataLocation.Ssf, "stranger.example");
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => transmitters.Verifier.VerifyAsync(Set(stranger)));
        Assert.Equal(SsfSetVerificationException.InvalidIssuer, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => transmitters.Verifier.VerifyAsync("not a jwt"));
        Assert.Equal(SsfSetVerificationException.InvalidRequest, ex.ErrorCode);
        // a SET without an issuer is not a SET (RFC 8417 requires iss): invalid_request, not
        // invalid_issuer, which is for an issuer the receiver does not know
        JsonObject claims = _keycloak.SetClaims("CaepSessionRevoked", Opaque("session-1"));
        claims.Remove("iss");
        ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => transmitters.Verifier.VerifyAsync(_keycloak.SignSet(claims)));
        Assert.Equal(SsfSetVerificationException.InvalidRequest, ex.ErrorCode);
    }

    [Fact]
    public async Task PushAuthenticatesEveryTransmitterWithItsOwnHeader()
    {
        SsfTransmitters transmitters = Transmitters();
        SsfSetProcessor processor = new(transmitters.Verifier, new InMemorySsfJtiDedupStore(10),
            [SsfEventHandler.Of((_, _) => Task.CompletedTask)]);
        SsfPushHandler pushHandler = new(processor, transmitters.PushAuthorizationHeader);
        Assert.Equal(202, (await pushHandler.HandleAsync("Bearer keycloak-secret", Bytes(Set(_keycloak)))).Status);
        Assert.Equal(401, (await pushHandler.HandleAsync("Bearer okta-secret", Bytes(Set(_keycloak)))).Status);
        Assert.Equal(401, (await pushHandler.HandleAsync(null, Bytes(Set(_keycloak)))).Status);
        // okta expects no header
        Assert.Equal(202, (await pushHandler.HandleAsync(null, Bytes(Set(_okta)))).Status);
        Assert.Equal(400, (await pushHandler.HandleAsync(null, Bytes("not a jwt"))).Status);
    }

    [Fact]
    public void LooksUpTransmittersByNameAndIssuer()
    {
        SsfTransmitters transmitters = Transmitters();
        Assert.Equal(["keycloak", "okta"], transmitters.All.Select(t => t.Name));
        Assert.Equal(_okta.Issuer, transmitters.Get("okta")!.Issuer);
        Assert.Equal("keycloak", transmitters.ByIssuer(_keycloak.Issuer)!.Name);
        Assert.Null(transmitters.Get("auth0"));
        Assert.Null(transmitters.Primary);
        Assert.NotNull(transmitters.StreamVerification(_okta.Issuer));
        Assert.Null(transmitters.StreamVerification("https://other.example"));
        Assert.Equal("okta", transmitters.NameOf(_okta.Issuer));
        Assert.Equal("https://other.example", transmitters.NameOf("https://other.example"));
        Assert.Empty(transmitters.CriticalSubjectMembers(_okta.Issuer));
        Assert.Equal("keycloak (" + _keycloak.Issuer + ")", transmitters.Get("keycloak")!.ToString());
    }

    [Fact]
    public void PrimaryIsTheOnlyOrTheDefaultTransmitter()
    {
        SsfTransmitter only = Transmitter("okta", _okta, null);
        Assert.Same(only, new SsfTransmitters([only]).Primary);
        SsfTransmitter dflt = Transmitter(SsfTransmitter.DefaultName, _keycloak, null);
        Assert.Same(dflt, new SsfTransmitters([only, dflt]).Primary);
    }

    [Fact]
    public void RejectsDuplicateNamesAndIssuers()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new SsfTransmitters([Transmitter("a", _keycloak, null), Transmitter("a", _okta, null)]));
        Assert.Contains("named 'a'", ex.Message);
        ex = Assert.Throws<ArgumentException>(
            () => new SsfTransmitters([Transmitter("a", _keycloak, null), Transmitter("b", _keycloak, null)]));
        Assert.Contains("same issuer", ex.Message);
        Assert.Throws<ArgumentException>(() => new SsfTransmitters([]));
    }

    [Fact]
    public void BuilderRequiresResolverAndVerifier()
    {
        Assert.Throws<ArgumentNullException>(() => new SsfTransmitterBuilder("a", _keycloak.Issuer).Build());
    }

    private SsfTransmitters Transmitters()
    {
        return new SsfTransmitters([Transmitter("keycloak", _keycloak, "Bearer keycloak-secret"), Transmitter("okta", _okta, null)]);
    }

    private SsfTransmitter Transmitter(string name, TestTransmitter transmitter, string? pushHeader)
    {
        HttpClient http = transmitter.CreateHttpClient();
        _disposables.Add(http);
        SsfTransmitterMetadataResolver metadataResolver = new(transmitter.Issuer, null, http);
        _disposables.Add(metadataResolver);
        SsfSetVerifierOptions options = new();
        options.ExpectedAudiences.Add(TestTransmitter.Audience);
        JwtSsfSetVerifier verifier = new(transmitter.Issuer,
            async ct => (await metadataResolver.ResolveAsync(ct)).JwksUri!, http, options);
        _disposables.Add(verifier);
        return new SsfTransmitterBuilder(name, transmitter.Issuer)
        {
            MetadataResolver = metadataResolver,
            Verifier = verifier,
            PushAuthorizationHeader = pushHeader,
        }.Build();
    }

    private static string Set(TestTransmitter transmitter)
    {
        return transmitter.Set("CaepSessionRevoked", Opaque("session-1"));
    }

    private static byte[] Bytes(string set)
    {
        return Encoding.UTF8.GetBytes(set);
    }
}
