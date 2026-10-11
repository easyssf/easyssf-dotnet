using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Set;

namespace EasySsf.Receiver.Tests.Set;

public class InMemorySsfJtiDedupStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T10:00:00Z");

    private readonly TestClock _clock = new(Now);

    private readonly InMemorySsfJtiDedupStore _store;

    public InMemorySsfJtiDedupStoreTests()
    {
        _store = new InMemorySsfJtiDedupStore(10, timeProvider: _clock);
    }

    [Fact]
    public async Task ClaimInProgressProcessedForget()
    {
        SsfJtiClaim claim = await _store.ClaimAsync(Set("jti-1"));
        Assert.True(claim.IsNew);
        Assert.Equal(SsfJtiClaim.InProgress, await _store.ClaimAsync(Set("jti-1")));
        await _store.ProcessedAsync(Set("jti-1"), claim);
        Assert.Equal(SsfJtiClaim.Processed, await _store.ClaimAsync(Set("jti-1")));
        // a processed SET is not forgotten, the claim is over
        await _store.ForgetAsync(Set("jti-1"), claim);
        Assert.Equal(SsfJtiClaim.Processed, await _store.ClaimAsync(Set("jti-1")));
        SsfJtiClaim second = await _store.ClaimAsync(Set("jti-2"));
        await _store.ForgetAsync(Set("jti-2"), second);
        Assert.Equal(SsfJtiClaimState.New, (await _store.ClaimAsync(Set("jti-2"))).State);
        // a jti is only unique per issuer
        Assert.Equal(SsfJtiClaimState.New, (await _store.ClaimAsync(Set("https://other.example", "jti-1"))).State);
        Assert.Equal(3, _store.Count);
    }

    [Fact]
    public async Task AbandonedClaimIsTakenOverAfterTheLease()
    {
        InMemorySsfJtiDedupStore store = new(10, TimeSpan.FromSeconds(30), _clock);
        SsfJtiClaim first = await store.ClaimAsync(Set("jti-1"));
        Assert.True(first.IsNew);
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SsfJtiClaim.InProgress, await store.ClaimAsync(Set("jti-1")));
        _clock.Advance(TimeSpan.FromSeconds(1));
        SsfJtiClaim second = await store.ClaimAsync(Set("jti-1"));
        Assert.True(second.IsNew);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(SsfJtiClaim.InProgress, await store.ClaimAsync(Set("jti-1")));
        // processed rows do not expire with the lease
        await store.ProcessedAsync(Set("jti-1"), second);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(SsfJtiClaim.Processed, await store.ClaimAsync(Set("jti-1")));
    }

    [Fact]
    public async Task LateHolderCanNeitherCompleteNorForgetTheClaimTakenOverFromIt()
    {
        InMemorySsfJtiDedupStore store = new(10, TimeSpan.FromSeconds(30), _clock);
        SsfJtiClaim late = await store.ClaimAsync(Set("jti-1"));
        _clock.Advance(TimeSpan.FromSeconds(30));
        SsfJtiClaim current = await store.ClaimAsync(Set("jti-1"));
        Assert.True(current.IsNew);
        // the late holder's handlers succeed: the SET still belongs to the current holder
        await store.ProcessedAsync(Set("jti-1"), late);
        Assert.Equal(SsfJtiClaim.InProgress, await store.ClaimAsync(Set("jti-1")));
        // the late holder's handlers fail: the current claim stays
        await store.ForgetAsync(Set("jti-1"), late);
        Assert.Equal(SsfJtiClaim.InProgress, await store.ClaimAsync(Set("jti-1")));
        await store.ProcessedAsync(Set("jti-1"), current);
        Assert.Equal(SsfJtiClaim.Processed, await store.ClaimAsync(Set("jti-1")));
    }

    [Fact]
    public async Task SuccessOfAnEvictedClaimIsRecorded()
    {
        InMemorySsfJtiDedupStore small = new(1);
        SsfJtiClaim claim = await small.ClaimAsync(Set("jti-1"));
        await small.ClaimAsync(Set("jti-2"));
        Assert.Equal(1, small.Count);
        await small.ProcessedAsync(Set("jti-1"), claim);
        Assert.Equal(SsfJtiClaim.Processed, await small.ClaimAsync(Set("jti-1")));
    }

    [Fact]
    public async Task EvictsOldestEntryWhenFull()
    {
        InMemorySsfJtiDedupStore store = new(2);
        Assert.True((await store.ClaimAsync(Set("jti-1"))).IsNew);
        Assert.True((await store.ClaimAsync(Set("jti-2"))).IsNew);
        Assert.True((await store.ClaimAsync(Set("jti-3"))).IsNew);
        Assert.Equal(2, store.Count);
        Assert.Equal(SsfJtiClaim.InProgress, await store.ClaimAsync(Set("jti-3")));
        Assert.True((await store.ClaimAsync(Set("jti-1"))).IsNew);
    }

    [Fact]
    public void RejectsNonPositiveLease()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySsfJtiDedupStore(10, TimeSpan.Zero));
        Assert.Equal(InMemorySsfJtiDedupStore.DefaultLease, new InMemorySsfJtiDedupStore(0).Lease);
    }

    private static SsfEventToken Set(string jti)
    {
        return Set("https://idp.example", jti);
    }

    private static SsfEventToken Set(string issuer, string jti)
    {
        return new SsfEventToken(jti, issuer, Now, null, [], null, null, SsfJson.EmptyObject);
    }
}
