using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;
using EasySsf.Testing;

namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// Several receivers (instances of one application) polling the same stream and sharing the
/// database stores: a SET is handled by one of them and skipped by the others, and an
/// acknowledgement one instance could not deliver leaves with the next poll of another.
/// </summary>
public sealed class DbSharedReceiversTests
{
    private const string Prefix = DbSsfSchema.DefaultTablePrefix;

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetIsHandledOnceAndAcknowledgedByWhicheverInstancePollsNext(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver a = setup.Receiver();
        Receiver b = setup.Receiver();
        string jti = setup.Queue("session-1");
        // a handles the SET, then loses the transmitter before the acknowledgement leaves
        a.AfterHandling = () =>
        {
            setup.Transmitter.Available = false;
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<SsfPollException>(() => a.Poller.PollNowAsync());

        Assert.Equal([jti], a.Handled);
        Assert.Empty(setup.Transmitter.AcknowledgedSets);
        // the acknowledgement waits in the shared store, visible to both instances
        Assert.Equal(1, await a.Poller.GetPendingAckCountAsync());
        Assert.Equal(1, await b.Poller.GetPendingAckCountAsync());

        // b polls: its request carries a's acknowledgement from the shared store, so the
        // transmitter releases the SET before answering and b does not even see it
        setup.Transmitter.Available = true;
        Assert.Equal(0, await b.Poller.PollNowAsync());

        Assert.Empty(b.Handled);
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
        Assert.Equal(0, await a.Poller.GetPendingAckCountAsync());
        Assert.Equal(0, await b.Poller.GetPendingAckCountAsync());
        Assert.Equal(0, setup.Transmitter.QueuedSetCount);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ErrorReportOfOneInstanceLeavesWithTheNextPollOfAnother(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver a = setup.Receiver();
        Receiver b = setup.Receiver();
        string jti = setup.Queue("session-1");
        setup.Transmitter.QueueSet("broken", "not-a-set");
        a.AfterHandling = () =>
        {
            setup.Transmitter.Available = false;
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<SsfPollException>(() => a.Poller.PollNowAsync());
        Assert.Empty(setup.Transmitter.ReportedErrors);

        setup.Transmitter.Available = true;
        await b.Poller.PollNowAsync();

        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
        Assert.Equal(["broken"], setup.Transmitter.ReportedErrors.Keys);
        Assert.Empty(b.Handled);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetDeliveredAgainLaterIsNotHandledTwiceByAnyInstance(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver a = setup.Receiver();
        Receiver b = setup.Receiver();
        string set = setup.Transmitter.Set("CaepSessionRevoked", SsfSubjectIdentifiers.Opaque("session-1"));
        string jti = Jti(set);
        setup.Transmitter.QueueSet(set);
        Assert.Equal(1, await a.Poller.PollNowAsync());
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);

        // the transmitter delivers the same SET once more, to the other instance
        setup.Transmitter.QueueSet(jti, set);
        Assert.Equal(1, await b.Poller.PollNowAsync());

        Assert.Equal([jti], a.Handled);
        Assert.Empty(b.Handled);
        Assert.Equal([jti, jti], setup.Transmitter.AcknowledgedSets);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetBeingHandledByOneInstanceIsNeitherHandledNorAcknowledgedByAnother(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver a = setup.Receiver();
        Receiver b = setup.Receiver();
        string jti = setup.Queue("session-1");
        TaskCompletionSource aIsHandling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bHasPolled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // a's handler blocks until b has polled, then fails
        a.AfterHandling = async () =>
        {
            aIsHandling.SetResult();
            await bHasPolled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("store is down");
        };
        // the failed handler leaves the SET unacknowledged
        Task<int> aPolls = Task.Run(() => a.Poller.PollNowAsync());
        await aIsHandling.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // b receives the SET while a is still on it: in progress, neither handled nor acknowledged
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Empty(b.Handled);
        Assert.Empty(setup.Transmitter.AcknowledgedSets);
        bHasPolled.SetResult();
        await aPolls.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(setup.Transmitter.AcknowledgedSets);

        // the redelivery is handled, by whichever instance polls
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Equal([jti], b.Handled);
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetHandledByAnInstanceThatDiedBeforeRecordingTheSuccessIsHandledAgainAfterTheLease(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        // a's instance dies between its handler's side effects and the record of the success
        Receiver a = setup.Receiver(new DyingDedupStore(setup.DedupStore));
        Receiver b = setup.Receiver(setup.NewDedupStore());
        string jti = setup.Queue("session-1");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => a.Poller.PollNowAsync());
        Assert.Contains("died", ex.Message);
        Assert.Equal([jti], a.Handled);
        Assert.Empty(setup.Transmitter.AcknowledgedSets);
        Assert.Equal("IN_PROGRESS", await setup.StateAsync(jti));

        // within the lease: b leaves the SET alone
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Empty(b.Handled);
        Assert.Empty(setup.Transmitter.AcknowledgedSets);

        // after the lease: b takes the claim over and handles the SET a second time, at least once
        setup.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Equal([jti], b.Handled);
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
        Assert.Equal("PROCESSED", await setup.StateAsync(jti));
        // handled twice in total, with the same idempotency key both times
        Assert.Equal(a.Keys, b.Keys);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task LateFailureOfTheFirstInstanceDoesNotForgetTheSetTheSecondHandledAfterTheLease(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver a = setup.Receiver();
        Receiver b = setup.Receiver();
        string jti = setup.Queue("session-1");
        TaskCompletionSource aIsHandling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bHasHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // a's handler outlives the lease, then fails
        a.AfterHandling = async () =>
        {
            aIsHandling.SetResult();
            await bHasHandled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("store is down");
        };
        // the failed handler leaves the SET unacknowledged by a
        Task<int> aPolls = Task.Run(() => a.Poller.PollNowAsync());
        await aIsHandling.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // the lease expires while a is still on the SET: b takes it over and handles it
        setup.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Equal([jti], b.Handled);
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
        Assert.Equal("PROCESSED", await setup.StateAsync(jti));

        // a's late failure must not forget b's success
        bHasHandled.SetResult();
        await aPolls.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("PROCESSED", await setup.StateAsync(jti));
        Assert.Equal(SsfJtiClaim.Processed, await setup.DedupStore.ClaimAsync(setup.Token(jti)));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetClaimedByAnInstanceThatDiedBeforeItsHandlerRanIsHandledAfterTheLease(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        Receiver b = setup.Receiver(setup.NewDedupStore());
        string jti = setup.Queue("session-1");
        // the dead instance's claim, left behind in the shared table
        Assert.True((await setup.DedupStore.ClaimAsync(setup.Token(jti))).IsNew);

        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Empty(b.Handled);
        Assert.Empty(setup.Transmitter.AcknowledgedSets);

        setup.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await b.Poller.PollNowAsync());
        Assert.Equal([jti], b.Handled);
        Assert.Equal([jti], setup.Transmitter.AcknowledgedSets);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ConcurrentClaimsOfOneSetYieldExactlyOneNew(string database)
    {
        await using Setup setup = await Setup.CreateAsync(database);
        string jti = "jti-concurrent";
        Assert.Single(await ClaimConcurrentlyAsync(setup, jti, 8));
        Assert.Equal("IN_PROGRESS", await setup.StateAsync(jti));

        // the lease expired: exactly one of the concurrent take-overs wins
        setup.Clock.Advance(TimeSpan.FromSeconds(61));
        List<SsfJtiClaim> takenOver = await ClaimConcurrentlyAsync(setup, jti, 8);
        Assert.Single(takenOver);

        await setup.DedupStore.ProcessedAsync(setup.Token(jti), takenOver[0]);
        Assert.Empty(await ClaimConcurrentlyAsync(setup, jti, 8));
        Assert.Equal("PROCESSED", await setup.StateAsync(jti));
    }

    /// <summary>
    /// Claims the SET from several tasks at the same moment, each with a store of its own.
    /// </summary>
    /// <returns>the claims that came back new</returns>
    private static async Task<List<SsfJtiClaim>> ClaimConcurrentlyAsync(Setup setup, string jti, int tasks)
    {
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task<SsfJtiClaim>> claims = [];
        for (int i = 0; i < tasks; i++)
        {
            DbSsfJtiDedupStore store = setup.NewDedupStore();
            claims.Add(Task.Run(async () =>
            {
                await start.Task;
                return await store.ClaimAsync(setup.Token(jti));
            }));
        }
        start.SetResult();
        SsfJtiClaim[] results = await Task.WhenAll(claims).WaitAsync(TimeSpan.FromSeconds(30));
        return results.Where(claim => claim.IsNew).ToList();
    }

    /// <summary>
    /// The <c>jti</c> of a SET, read from its payload.
    /// </summary>
    private static string Jti(string encodedSet)
    {
        return TestTransmitter.Claims(encodedSet)["jti"]!.GetValue<string>();
    }

    /// <summary>
    /// One instance of the application: its poller and what its handler saw.
    /// </summary>
    private sealed class Receiver
    {
        public SsfPoller Poller { get; set; } = null!;

        public List<string> Handled { get; } = [];

        public List<string> Keys { get; } = [];

        /// <summary>runs after a SET was handled, before it is acknowledged</summary>
        public Func<Task> AfterHandling { get; set; } = () => Task.CompletedTask;
    }

    /// <summary>
    /// A dedup store whose instance dies between the handler's side effects and the record of the
    /// success.
    /// </summary>
    private sealed class DyingDedupStore(ISsfJtiDedupStore shared) : ISsfJtiDedupStore
    {
        public ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default)
        {
            return shared.ClaimAsync(eventToken, cancellationToken);
        }

        public ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("the instance died");
        }

        public ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
        {
            return shared.ForgetAsync(eventToken, claim, cancellationToken);
        }
    }

    /// <summary>
    /// One transmitter and one database with the shared tables per test. The clock starts at the
    /// real time so that the SETs of the transmitter are not in the future, and the tests move it
    /// past the lease.
    /// </summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly List<Receiver> _receivers = [];

        private Setup(TestDatabase database)
        {
            Database = database;
            Transmitter = new TestTransmitter();
            Http = Transmitter.CreateHttpClient();
            DedupStore = NewDedupStore();
            AckStore = new DbSsfPollAckStore(database.Db, Prefix);
        }

        public TestDatabase Database { get; }

        public TestTransmitter Transmitter { get; }

        public HttpClient Http { get; }

        public TestClock Clock { get; } = new();

        public DbSsfJtiDedupStore DedupStore { get; }

        public DbSsfPollAckStore AckStore { get; }

        public static async Task<Setup> CreateAsync(string database)
        {
            TestDatabase db = await TestDatabase.OpenAsync(database);
            await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix), true, null);
            await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.PollAckTable(Prefix), DbSsfSchema.CreatePollAckTable(Prefix), true, null);
            return new Setup(db);
        }

        public DbSsfJtiDedupStore NewDedupStore()
        {
            return new DbSsfJtiDedupStore(Database.Db, Prefix, Clock);
        }

        public async Task<string> StateAsync(string jti)
        {
            return (await Database.QueryAsync("SELECT STATE FROM EASYSSF_PROCESSED_SET WHERE JTI = ?", reader => reader.GetString(0), jti))[0];
        }

        public SsfEventToken Token(string jti)
        {
            return new SsfEventToken(jti, Transmitter.Issuer, DateTimeOffset.UnixEpoch, null, [], null, null, SsfJson.EmptyObject);
        }

        public Receiver Receiver()
        {
            return Receiver(DedupStore);
        }

        public Receiver Receiver(ISsfJtiDedupStore dedupStore)
        {
            Receiver receiver = new();
            JwtSsfSetVerifier verifier = new(Transmitter.Issuer, new Uri(Transmitter.JwksUri), Http);
            ISsfEventHandler handler = SsfEventHandler.Of(async (eventContext, _) =>
            {
                receiver.Handled.Add(eventContext.EventToken.Jti);
                receiver.Keys.Add(eventContext.IdempotencyKey);
                await receiver.AfterHandling();
            });
            SsfSetProcessor processor = new(verifier, dedupStore, [handler]);
            SsfPollerOptions options = new() { AckStore = AckStore, Transmitter = Transmitter.Issuer, FlushOnStop = false };
            receiver.Poller = new SsfPoller(Http, SsfTransmitterTokenProviders.Fixed(TestTransmitter.DefaultAccessToken),
                () => new Uri(Transmitter.PollUri), processor, options);
            _receivers.Add(receiver);
            return receiver;
        }

        public string Queue(string sessionId)
        {
            string set = Transmitter.Set("CaepSessionRevoked", SsfSubjectIdentifiers.Opaque(sessionId));
            Transmitter.QueueSet(set);
            return Jti(set);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Receiver receiver in _receivers)
            {
                await receiver.Poller.StopAsync();
                receiver.Poller.Dispose();
            }
            Http.Dispose();
            Transmitter.Dispose();
            await Database.DisposeAsync();
        }
    }
}
