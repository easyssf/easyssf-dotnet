using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Receiver.Set;
using EasySsf.Testing;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Set;

public sealed class IssuerRoutingSsfSetVerifierTests : IClassFixture<TestTransmitter>
{
    private readonly TestTransmitter _transmitter;

    private readonly RecordingVerifier _recording = new();

    private readonly IssuerRoutingSsfSetVerifier _verifier;

    public IssuerRoutingSsfSetVerifierTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _verifier = new IssuerRoutingSsfSetVerifier(new Dictionary<string, ISsfSetVerifier>
        {
            [transmitter.Issuer] = _recording,
        });
    }

    [Fact]
    public void RequiresAVerifier()
    {
        Assert.Throws<ArgumentException>(() => new IssuerRoutingSsfSetVerifier([]));
    }

    [Fact]
    public async Task RejectsSetThatCannotBeParsed()
    {
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(
            () => _verifier.VerifyAsync("not-a-jwt"));
        Assert.Equal(SsfSetVerificationException.InvalidRequest, ex.ErrorCode);
        JsonObject claims = SessionRevoked();
        claims.Remove("iss");
        ex = await Assert.ThrowsAsync<SsfSetVerificationException>(() => _verifier.VerifyAsync(_transmitter.SignSet(claims)));
        Assert.Equal(SsfSetVerificationException.InvalidRequest, ex.ErrorCode);
        Assert.Empty(_recording.Sets);
    }

    [Fact]
    public async Task RejectsSetOfUnknownIssuer()
    {
        JsonObject claims = SessionRevoked();
        claims["iss"] = "https://other.example";
        SsfSetVerificationException ex = await Assert.ThrowsAsync<SsfSetVerificationException>(
            () => _verifier.VerifyAsync(_transmitter.SignSet(claims)));
        Assert.Equal(SsfSetVerificationException.InvalidIssuer, ex.ErrorCode);
        Assert.Contains("https://other.example", ex.Message);
        Assert.Empty(_recording.Sets);
    }

    [Fact]
    public async Task RoutesSetToTheVerifierOfItsIssuer()
    {
        string set = _transmitter.SignSet(SessionRevoked());
        SsfEventToken token = await _verifier.VerifyAsync(set);
        Assert.Equal([set], _recording.Sets);
        Assert.Equal(_transmitter.Issuer, token.Iss);
        Assert.Equal(_transmitter.Issuer, SsfSetClaims.UnverifiedIssuer(set));
        Assert.Null(SsfSetClaims.UnverifiedIssuer(null));
        Assert.Null(SsfSetClaims.UnverifiedIssuer("a.b"));
    }

    private JsonObject SessionRevoked()
    {
        return _transmitter.SetClaims("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
    }

    private sealed class RecordingVerifier : ISsfSetVerifier
    {
        public List<string> Sets { get; } = [];

        public Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
        {
            Sets.Add(encodedSet);
            JsonObject claims = TestTransmitter.Claims(encodedSet);
            return Task.FromResult(new SsfEventToken(claims["jti"]!.GetValue<string>(), claims["iss"]!.GetValue<string>(),
                DateTimeOffset.UnixEpoch, null, [], null, null, Core.Support.SsfJson.ToElement(claims)));
        }
    }
}
