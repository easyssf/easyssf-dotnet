using EasySsf.Receiver.Poll;

namespace EasySsf.Receiver.Tests.Poll;

public class InMemorySsfPollAckStoreTests
{
    private const string Issuer = "https://idp.example";

    private readonly InMemorySsfPollAckStore _store = new();

    [Fact]
    public async Task KeepsAcknowledgementsAndErrorsInOrderPerIssuer()
    {
        await _store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-1"));
        await _store.RecordAsync(Issuer, SsfPendingAck.Error("jti-2", "invalid_request", "not a SET"));
        await _store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-3"));
        await _store.RecordAsync("https://other.example", SsfPendingAck.Ack("jti-1"));

        Assert.Equal(3, await _store.CountAsync(Issuer));
        Assert.Equal(["jti-1", "jti-2", "jti-3"], (await _store.PendingAsync(Issuer, 10)).Select(ack => ack.Jti));
        Assert.Equal(["jti-1", "jti-2"], (await _store.PendingAsync(Issuer, 2)).Select(ack => ack.Jti));
        Assert.True((await _store.PendingAsync(Issuer, 10))[1].IsError);
        Assert.Equal("not a SET", (await _store.PendingAsync(Issuer, 10))[1].ErrorDescription);
        Assert.Equal(1, await _store.CountAsync("https://other.example"));
        Assert.Equal(0, await _store.CountAsync("https://unknown.example"));
    }

    [Fact]
    public async Task RecordingTheSameSetAgainChangesNothing()
    {
        await _store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-1"));
        await _store.RecordAsync(Issuer, SsfPendingAck.Error("jti-1", "invalid_request", null));
        SsfPendingAck ack = Assert.Single(await _store.PendingAsync(Issuer, 10));
        Assert.False(ack.IsError);
    }

    [Fact]
    public async Task RemovesWhatTheTransmitterReceived()
    {
        await _store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-1"));
        await _store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-2"));
        await _store.RemoveAsync(Issuer, ["jti-1", "jti-unknown"]);
        Assert.Equal(["jti-2"], (await _store.PendingAsync(Issuer, 10)).Select(ack => ack.Jti));
    }

    [Fact]
    public void PendingAckIsValidated()
    {
        Assert.ThrowsAny<ArgumentException>(() => SsfPendingAck.Error("jti", null!, "x"));
        Assert.Throws<ArgumentException>(() => new SsfPendingAck("jti", null, "description without code"));
        Assert.Throws<ArgumentException>(() => SsfPendingAck.Ack(""));
        Assert.False(SsfPendingAck.Ack("jti").IsError);
    }
}
