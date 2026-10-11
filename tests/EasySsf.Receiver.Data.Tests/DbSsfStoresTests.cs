using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;

namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// The tests of the database stores, on SQLite and, with <c>EASYSSF_DATABASE_TESTS</c> set, on a
/// PostgreSQL container, see <see cref="TestDatabase"/>.
/// </summary>
public sealed class DbSsfStoresTests
{
    private const string Issuer = "https://idp.example";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T10:00:00Z");

    private const string Prefix = DbSsfSchema.DefaultTablePrefix;

    [Fact]
    public void SchemaFileMatchesTheStatementsThatCreateTheTables()
    {
        List<string> expected = [.. DbSsfSchema.CreateProcessedSetTable(Prefix), .. DbSsfSchema.CreateRevocationTable(Prefix), .. DbSsfSchema.CreatePollAckTable(Prefix)];
        Assert.Equal(expected.Select(Normalize), DbSsfSchema.Statements(DbSsfSchema.SchemaSql));
        Assert.Equal(["V0_1_0__processed_sets_and_revocations.sql", "V0_3_0__dedup_state_and_poll_acks.sql", "V0_4_0__state_changed_at.sql"],
            DbSsfSchema.Migrations.Select(migration => migration.Name));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task MigrationScriptsLeadToTheCurrentSchema(string database)
    {
        await using TestDatabase fresh = await TestDatabase.OpenAsync(database);
        foreach (DbSsfMigration migration in DbSsfSchema.Migrations)
        {
            foreach (string statement in DbSsfSchema.Statements(migration.Sql))
            {
                await fresh.ExecuteAsync(statement);
            }
        }
        // every table has every column of this release: nothing to create or upgrade
        await DbSsfSchema.PrepareTableAsync(fresh.Db, DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix),
            DbSsfSchema.ProcessedSetUpgrades(Prefix), false, null);
        await DbSsfSchema.PrepareTableAsync(fresh.Db, DbSsfSchema.RevocationTable(Prefix), DbSsfSchema.CreateRevocationTable(Prefix), false, null);
        await DbSsfSchema.PrepareTableAsync(fresh.Db, DbSsfSchema.PollAckTable(Prefix), DbSsfSchema.CreatePollAckTable(Prefix), false, null);
        Assert.True((await new DbSsfJtiDedupStore(fresh.Db, Prefix).ClaimAsync(Set("jti-1"))).IsNew);
        DbSsfPollAckStore acks = new(fresh.Db, Prefix);
        await acks.RecordAsync(Issuer, SsfPendingAck.Ack("jti-1"));
        Assert.Equal(1, await acks.CountAsync(Issuer));
        await new DbSsfTokenRevocationStore(fresh.Db, TimeSpan.FromMinutes(1), Prefix).RevokeSessionAsync(Issuer, "s");
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task MissingColumnsAreAddedWhereTablesMayBeCreatedAndReportedOtherwise(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        TestDatabase db = stores.Database;
        await db.ExecuteAsync("DROP TABLE EASYSSF_PROCESSED_SET");
        foreach (string statement in DbSsfSchema.Statements(DbSsfSchema.Migrations[0].Sql).Where(s => s.Contains("EASYSSF_PROCESSED_SET")))
        {
            await db.ExecuteAsync(statement);
        }
        IReadOnlyList<DbSsfSchemaUpgrade> upgrades = DbSsfSchema.ProcessedSetUpgrades(Prefix);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DbSsfSchema.PrepareTableAsync(db.Db,
            DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix), upgrades, false, "or else").AsTask());
        Assert.Contains("[STATE, STATE_CHANGED_AT]", ex.Message);
        Assert.Contains("ALTER TABLE EASYSSF_PROCESSED_SET ADD STATE", ex.Message);
        Assert.Contains("ALTER TABLE EASYSSF_PROCESSED_SET RENAME COLUMN PROCESSED_AT TO STATE_CHANGED_AT", ex.Message);
        Assert.Contains("migration/", ex.Message);
        Assert.Contains("or else", ex.Message);

        await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix), upgrades, true, null);
        // repeatable
        await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix), upgrades, true, null);
        Assert.True((await stores.DedupStore().ClaimAsync(Set("jti-1"))).IsNew);
        // the index moved with the column: the retention purge still uses it
        Assert.Single(await db.QueryAsync("SELECT STATE_CHANGED_AT FROM EASYSSF_PROCESSED_SET", reader => Convert.ToInt64(reader.GetValue(0))));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task TableOf0dot3dot0IsUpgradedByRenamingTheColumn(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        TestDatabase db = stores.Database;
        await db.ExecuteAsync("DROP TABLE EASYSSF_PROCESSED_SET");
        foreach (DbSsfMigration migration in DbSsfSchema.Migrations.Take(2))
        {
            foreach (string statement in DbSsfSchema.Statements(migration.Sql).Where(s => s.Contains("EASYSSF_PROCESSED_SET")))
            {
                await db.ExecuteAsync(statement);
            }
        }
        // a row of the old release keeps its timestamp under the new name (and within the retention)
        long processedAt = Now.AddSeconds(-60).ToUnixTimeMilliseconds();
        await db.UpdateAsync("INSERT INTO EASYSSF_PROCESSED_SET (ISSUER, JTI, STATE, PROCESSED_AT) VALUES (?, ?, ?, ?)", Issuer, "old", "PROCESSED", processedAt);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stores.DedupStore().PrepareTableAsync(false).AsTask());
        Assert.Contains("[STATE_CHANGED_AT]", ex.Message);
        Assert.Contains("RENAME COLUMN PROCESSED_AT TO STATE_CHANGED_AT", ex.Message);
        await stores.DedupStore().PrepareTableAsync(true);
        Assert.Equal([processedAt], await db.QueryAsync("SELECT STATE_CHANGED_AT FROM EASYSSF_PROCESSED_SET WHERE JTI = ?",
            reader => Convert.ToInt64(reader.GetValue(0)), "old"));
        Assert.Equal(SsfJtiClaim.Processed, await stores.DedupStore().ClaimAsync(Set("old")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task PreparingTablesIsRepeatableAndReportsMissingTables(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        ISsfDbOperations db = stores.Database.Db;
        await DbSsfSchema.PrepareTableAsync(db, DbSsfSchema.RevocationTable(Prefix), DbSsfSchema.CreateRevocationTable(Prefix), true, null);
        await DbSsfSchema.PrepareTableAsync(db, DbSsfSchema.RevocationTable(Prefix), DbSsfSchema.CreateRevocationTable(Prefix), false, null);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DbSsfSchema.PrepareTableAsync(db,
            DbSsfSchema.RevocationTable("OTHER_"), DbSsfSchema.CreateRevocationTable("OTHER_"), false, null).AsTask());
        Assert.Contains("OTHER_REVOCATION", ex.Message);
        Assert.Contains("schema.sql", ex.Message);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task TablePrefixIsConfigurableButRestricted(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        ISsfDbOperations db = stores.Database.Db;
        await DbSsfSchema.PrepareTableAsync(db, DbSsfSchema.ProcessedSetTable("APP_SSF_"), DbSsfSchema.CreateProcessedSetTable("APP_SSF_"), true, null);
        DbSsfJtiDedupStore store = new(db, "APP_SSF_");
        Assert.False(await SeenBeforeAsync(store, Set("jti-1")));
        Assert.Equal(1, await stores.Database.CountAsync("APP_SSF_PROCESSED_SET"));
        Assert.Throws<ArgumentException>(() => new DbSsfJtiDedupStore(db, "X; DROP TABLE Y;"));
        Assert.Throws<ArgumentException>(() => new DbSsfPollAckStore(db, "X Y"));
        Assert.Throws<ArgumentException>(() => new DbSsfTokenRevocationStore(db, TimeSpan.FromMinutes(1), "X-Y"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task SetIsSeenBeforeOnceItWasRecorded(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore store = stores.DedupStore();
        Assert.False(await SeenBeforeAsync(store, Set("jti-1")));
        Assert.True(await SeenBeforeAsync(store, Set("jti-1")));
        Assert.False(await SeenBeforeAsync(store, Set("jti-2")));
        // a jti is only unique per issuer
        Assert.False(await SeenBeforeAsync(store, Set("https://other.example", "jti-1")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ProcessedSetsAreSharedBetweenInstances(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        Assert.False(await SeenBeforeAsync(stores.DedupStore(), Set("jti-1")));
        Assert.True(await SeenBeforeAsync(stores.DedupStore(), Set("jti-1")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ClaimTellsWhetherAnotherInstanceIsOnTheSet(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore a = stores.DedupStore();
        DbSsfJtiDedupStore b = stores.DedupStore();
        SsfJtiClaim first = await a.ClaimAsync(Set("jti-1"));
        Assert.True(first.IsNew);
        // handled right now by a: b leaves it alone
        Assert.Equal(SsfJtiClaim.InProgress, await b.ClaimAsync(Set("jti-1")));
        Assert.Equal(SsfJtiClaim.InProgress, await a.ClaimAsync(Set("jti-1")));
        await a.ProcessedAsync(Set("jti-1"), first);
        Assert.Equal(SsfJtiClaim.Processed, await b.ClaimAsync(Set("jti-1")));
        // a failed handler forgets the claim, the redelivery is handled
        SsfJtiClaim second = await a.ClaimAsync(Set("jti-2"));
        Assert.True(second.IsNew);
        await a.ForgetAsync(Set("jti-2"), second);
        Assert.True((await b.ClaimAsync(Set("jti-2"))).IsNew);
        Assert.Equal("PROCESSED", await stores.StateAsync("jti-1"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task AbandonedClaimIsTakenOverAfterTheLease(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore crashed = stores.DedupStore();
        DbSsfJtiDedupStore survivor = stores.DedupStore();
        crashed.Lease = TimeSpan.FromSeconds(30);
        survivor.Lease = TimeSpan.FromSeconds(30);
        SsfJtiClaim abandoned = await crashed.ClaimAsync(Set("jti-1"));
        Assert.True(abandoned.IsNew);
        stores.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SsfJtiClaim.InProgress, await survivor.ClaimAsync(Set("jti-1")));
        stores.Clock.Advance(TimeSpan.FromSeconds(1));
        SsfJtiClaim takenOver = await survivor.ClaimAsync(Set("jti-1"));
        Assert.True(takenOver.IsNew);
        Assert.NotEqual(abandoned.Token, takenOver.Token);
        // the take-over renewed the claim
        Assert.Equal(SsfJtiClaim.InProgress, await crashed.ClaimAsync(Set("jti-1")));
        await survivor.ProcessedAsync(Set("jti-1"), takenOver);
        Assert.Equal(SsfJtiClaim.Processed, await crashed.ClaimAsync(Set("jti-1")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task LateInstanceCanNeitherCompleteNorForgetAClaimTakenOverFromIt(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore late = stores.DedupStore();
        DbSsfJtiDedupStore current = stores.DedupStore();
        SsfJtiClaim lateClaim = await late.ClaimAsync(Set("jti-1"));
        stores.Clock.Advance(TimeSpan.FromSeconds(60));
        SsfJtiClaim currentClaim = await current.ClaimAsync(Set("jti-1"));
        Assert.True(currentClaim.IsNew);
        // the late instance's handlers succeed after the lease: the SET belongs to the current
        // holder, whose claim stays as it is
        await late.ProcessedAsync(Set("jti-1"), lateClaim);
        Assert.Equal("IN_PROGRESS", await stores.StateAsync("jti-1"));
        Assert.Equal(SsfJtiClaim.InProgress, await late.ClaimAsync(Set("jti-1")));
        // the late instance's handlers fail: the current claim is not deleted
        await late.ForgetAsync(Set("jti-1"), lateClaim);
        Assert.Equal("IN_PROGRESS", await stores.StateAsync("jti-1"));
        await current.ProcessedAsync(Set("jti-1"), currentClaim);
        Assert.Equal("PROCESSED", await stores.StateAsync("jti-1"));
        // and a processed SET is not forgotten by a stale failure either
        await late.ForgetAsync(Set("jti-1"), lateClaim);
        await current.ForgetAsync(Set("jti-1"), currentClaim);
        Assert.Equal("PROCESSED", await stores.StateAsync("jti-1"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ProcessedAfterThePurgeRecordsTheSetAgain(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore store = stores.DedupStore();
        SsfJtiClaim claim = await store.ClaimAsync(Set("jti-1"));
        await stores.Database.UpdateAsync("DELETE FROM EASYSSF_PROCESSED_SET");
        await store.ProcessedAsync(Set("jti-1"), claim);
        Assert.Equal(SsfJtiClaim.Processed, await store.ClaimAsync(Set("jti-1")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task TableOfAnOldReleaseIsRejectedWithTheStatementsToRun(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        TestDatabase db = stores.Database;
        await db.ExecuteAsync("DROP TABLE EASYSSF_PROCESSED_SET");
        await db.ExecuteAsync("CREATE TABLE EASYSSF_PROCESSED_SET (ISSUER VARCHAR(255) NOT NULL, JTI VARCHAR(255) NOT NULL, "
            + "PROCESSED_AT BIGINT NOT NULL, CONSTRAINT EASYSSF_PROCESSED_SET_PK PRIMARY KEY (ISSUER, JTI))");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stores.DedupStore().PrepareTableAsync(false).AsTask());
        Assert.Contains("ALTER TABLE EASYSSF_PROCESSED_SET ADD STATE", ex.Message);
        Assert.Contains("RENAME COLUMN PROCESSED_AT TO STATE_CHANGED_AT", ex.Message);
        Assert.Contains("migration/", ex.Message);
        await db.ExecuteAsync("ALTER TABLE EASYSSF_PROCESSED_SET ADD STATE VARCHAR(16) DEFAULT 'PROCESSED' NOT NULL");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stores.DedupStore().PrepareTableAsync(false).AsTask());
        Assert.Contains("[STATE_CHANGED_AT]", ex.Message);
        await db.ExecuteAsync("ALTER TABLE EASYSSF_PROCESSED_SET RENAME COLUMN PROCESSED_AT TO STATE_CHANGED_AT");
        await stores.DedupStore().PrepareTableAsync(false);
        Assert.True((await stores.DedupStore().ClaimAsync(Set("jti-1"))).IsNew);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ForgottenSetIsNotSeenBefore(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore store = stores.DedupStore();
        SsfJtiClaim claim = await store.ClaimAsync(Set("jti-1"));
        await store.ForgetAsync(Set("jti-1"), claim);
        Assert.False(await SeenBeforeAsync(store, Set("jti-1")));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ProcessedSetsAreForgottenAfterTheRetentionTime(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore store = stores.DedupStore();
        await store.ProcessedAsync(Set("jti-1"), await store.ClaimAsync(Set("jti-1")));
        stores.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(SsfJtiClaim.Processed, await store.ClaimAsync(Set("jti-1")));
        stores.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True((await store.ClaimAsync(Set("jti-1"))).IsNew);
        Assert.Equal(1, await stores.Database.CountAsync("EASYSSF_PROCESSED_SET"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task RevokedSessionRevokesItsTokensWheneverTheyWereIssued(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfTokenRevocationStore store = stores.RevocationStore();
        await store.RevokeSessionAsync(Issuer, "session-1");
        Assert.True(await store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.True(await store.IsRevokedAsync(Issuer, "session-1", "alice", Now.AddSeconds(60)));
        Assert.False(await store.IsRevokedAsync(Issuer, "session-2", "alice", Now.AddSeconds(-60)));
        Assert.False(await store.IsRevokedAsync(Issuer, null, null, Now));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task RevokedSubjectRevokesTokensIssuedUpToTheRevocation(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfTokenRevocationStore store = stores.RevocationStore();
        await store.RevokeSubjectAsync(Issuer, "alice", Now);
        Assert.Equal(Now, await store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        Assert.True(await store.IsRevokedAsync(Issuer, "session-1", "alice", Now.AddSeconds(-60)));
        Assert.True(await store.IsRevokedAsync(Issuer, "session-1", "alice", Now));
        Assert.True(await store.IsRevokedAsync(Issuer, "session-1", "alice", null));
        Assert.False(await store.IsRevokedAsync(Issuer, "session-2", "alice", Now.AddSeconds(1)));
        Assert.False(await store.IsRevokedAsync(Issuer, "session-3", "bob", Now.AddSeconds(-60)));
        // a session and a subject may have the same identifier
        Assert.False(await store.IsSessionRevokedAsync(Issuer, "alice"));
        // revocations are scoped to the issuer
        Assert.Null(await store.GetSubjectRevokedAtAsync("https://other.example", "alice"));
        Assert.False(await store.IsRevokedAsync("https://other.example", "session-1", "alice", Now.AddSeconds(-60)));
        Assert.False(await store.IsRevokedAsync(null, "session-1", "alice", Now.AddSeconds(-60)));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task RevocationsAreSharedBetweenInstances(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        await stores.RevocationStore().RevokeSessionAsync(Issuer, "session-1");
        await stores.RevocationStore().RevokeSubjectAsync(Issuer, "alice", Now);
        // through the interface, so that the one-query override is what a caller gets
        Revocation.ISsfTokenRevocationStore other = stores.RevocationStore();
        Assert.True(await other.IsRevokedAsync(Issuer, "session-1", "bob", Now));
        Assert.True(await other.IsRevokedAsync(Issuer, "session-2", "alice", Now.AddSeconds(-1)));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task EventDeliveredLateDoesNotShortenMoreRecentRevocation(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfTokenRevocationStore store = stores.RevocationStore();
        await store.RevokeSubjectAsync(Issuer, "alice", Now);
        await store.RevokeSubjectAsync(Issuer, "alice", Now.AddSeconds(-120));
        Assert.Equal(Now, await store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        await store.RevokeSubjectAsync(Issuer, "alice", Now.AddSeconds(30));
        Assert.Equal(Now.AddSeconds(30), await store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        Assert.Equal(1, await stores.Database.CountAsync("EASYSSF_REVOCATION"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task RevocationsExpireAndAreRemoved(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfTokenRevocationStore store = stores.RevocationStore();
        await store.RevokeSessionAsync(Issuer, "session-1");
        await store.RevokeSubjectAsync(Issuer, "alice", Now);
        stores.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.Equal(Now, await store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        stores.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await store.IsSessionRevokedAsync(Issuer, "session-1"));
        Assert.Null(await store.GetSubjectRevokedAtAsync(Issuer, "alice"));
        Assert.False(await store.IsRevokedAsync(Issuer, "session-1", "alice", Now.AddSeconds(-60)));
        await store.RevokeSessionAsync(Issuer, "session-2");
        Assert.Equal(["session-2"], await stores.Database.QueryAsync("SELECT ID FROM EASYSSF_REVOCATION", reader => reader.GetString(0)));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task AcknowledgementsWaitUntilRemovedAndAreSharedBetweenInstances(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfPollAckStore store = stores.AckStore();
        await store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-1"));
        stores.Clock.Advance(TimeSpan.FromSeconds(1));
        await store.RecordAsync(Issuer, SsfPendingAck.Error("jti-2", "invalid_request", "not a SET"));
        await store.RecordAsync(Issuer, SsfPendingAck.Ack("jti-2"));
        await store.RecordAsync("https://other.example", SsfPendingAck.Ack("jti-1"));

        Assert.Equal(2, await stores.AckStore().CountAsync(Issuer));
        IReadOnlyList<SsfPendingAck> pending = await stores.AckStore().PendingAsync(Issuer, 10);
        Assert.Equal(["jti-1", "jti-2"], pending.Select(ack => ack.Jti));
        Assert.False(pending[0].IsError);
        Assert.Equal("invalid_request", pending[1].ErrorCode);
        Assert.Equal("not a SET", pending[1].ErrorDescription);
        Assert.Equal(["jti-1"], (await stores.AckStore().PendingAsync(Issuer, 1)).Select(ack => ack.Jti));

        await stores.AckStore().RemoveAsync(Issuer, ["jti-1", "jti-unknown"]);
        Assert.Equal(["jti-2"], (await stores.AckStore().PendingAsync(Issuer, 10)).Select(ack => ack.Jti));
        Assert.Equal(1, await stores.AckStore().CountAsync("https://other.example"));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ManyAcknowledgementsAreListedWithALimitAndRemovedInChunks(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfPollAckStore store = stores.AckStore();
        store.DeleteBatchSize = 50;
        for (int i = 0; i < 250; i++)
        {
            await store.RecordAsync(Issuer, SsfPendingAck.Ack($"jti-{i:000}"));
            stores.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.Equal(250, await store.CountAsync(Issuer));
        IReadOnlyList<SsfPendingAck> pending = await store.PendingAsync(Issuer, 120);
        Assert.Equal(120, pending.Count);
        Assert.Equal("jti-000", pending[0].Jti);
        Assert.Equal("jti-119", pending[119].Jti);

        await store.RemoveAsync(Issuer, pending.Select(ack => ack.Jti));
        Assert.Equal(130, await store.CountAsync(Issuer));
        Assert.Equal("jti-120", (await store.PendingAsync(Issuer, 1))[0].Jti);
        await store.RemoveAsync(Issuer, []);
        Assert.Equal(130, await store.CountAsync(Issuer));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task LongErrorDescriptionsAreCut(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfPollAckStore store = stores.AckStore();
        await store.RecordAsync(Issuer, SsfPendingAck.Error("jti-1", "invalid_request", new string('x', 5000)));
        Assert.Equal(1024, (await store.PendingAsync(Issuer, 1))[0].ErrorDescription!.Length);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task AcknowledgementsAreForgottenAfterTheRetentionTime(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfPollAckStore store = stores.AckStore();
        await store.RecordAsync(Issuer, SsfPendingAck.Ack("old"));
        stores.Clock.Advance(TimeSpan.FromHours(12));
        await store.RecordAsync(Issuer, SsfPendingAck.Ack("recent"));
        stores.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await store.PurgeExpiredAsync());
        Assert.Equal(["recent"], (await store.PendingAsync(Issuer, 10)).Select(ack => ack.Jti));
        // writing purges as well, at most once a minute
        stores.Clock.Advance(TimeSpan.FromDays(1));
        await store.RecordAsync(Issuer, SsfPendingAck.Ack("newer"));
        Assert.Equal(["newer"], (await store.PendingAsync(Issuer, 10)).Select(ack => ack.Jti));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task PurgeExpiredDeletesOnlyTheExpiredRows(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        // processed SETs are retained for a day in this test
        DbSsfJtiDedupStore dedupStore = stores.DedupStore();
        await SeenBeforeAsync(dedupStore, Set("old"));
        stores.Clock.Advance(TimeSpan.FromHours(12));
        await SeenBeforeAsync(dedupStore, Set("recent"));
        stores.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await dedupStore.PurgeExpiredAsync());
        Assert.Equal(["recent"], await stores.Database.QueryAsync("SELECT JTI FROM EASYSSF_PROCESSED_SET", reader => reader.GetString(0)));
        Assert.Equal(0, await dedupStore.PurgeExpiredAsync());

        // revocations for ten minutes
        DbSsfTokenRevocationStore revocationStore = stores.RevocationStore();
        await revocationStore.RevokeSessionAsync(Issuer, "old-session");
        stores.Clock.Advance(TimeSpan.FromMinutes(5));
        await revocationStore.RevokeSessionAsync(Issuer, "recent-session");
        stores.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await revocationStore.PurgeExpiredAsync());
        Assert.Equal(["recent-session"], await stores.Database.QueryAsync("SELECT ID FROM EASYSSF_REVOCATION", reader => reader.GetString(0)));
        Assert.Equal(0, await revocationStore.PurgeExpiredAsync());
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task CleanupPurgesAllStoresAndRunsOnlyWhenEnabled(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfJtiDedupStore dedupStore = stores.DedupStore();
        DbSsfTokenRevocationStore revocationStore = stores.RevocationStore();
        await SeenBeforeAsync(dedupStore, Set("old"));
        await revocationStore.RevokeSessionAsync(Issuer, "old-session");
        stores.Clock.Advance(TimeSpan.FromDays(2));

        using DbSsfStoreCleanup off = new([dedupStore, revocationStore], TimeSpan.Zero);
        Assert.False(off.IsEnabled);
        off.Start();
        Assert.False(off.IsRunning);
        Assert.Equal(2, await off.PurgeExpiredAsync());

        using DbSsfStoreCleanup cleanup = new([dedupStore, revocationStore], TimeSpan.FromMinutes(15));
        Assert.True(cleanup.IsEnabled);
        cleanup.Start();
        Assert.True(cleanup.IsRunning);
        await cleanup.StopAsync();
        Assert.False(cleanup.IsRunning);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task CleanupRunsPeriodicallyAndSurvivesAFailingStore(string database)
    {
        await using Stores stores = await Stores.CreateAsync(database);
        DbSsfTokenRevocationStore revocationStore = stores.RevocationStore();
        await revocationStore.RevokeSessionAsync(Issuer, "old-session");
        stores.Clock.Advance(TimeSpan.FromDays(2));
        // a store whose table is gone fails, the others are still purged
        DbSsfPollAckStore broken = new(stores.Database.Db, "MISSING_");
        using DbSsfStoreCleanup cleanup = new([broken, revocationStore], TimeSpan.FromMilliseconds(50));
        cleanup.Start();
        await Eventually.UntilAssertedAsync(async () => Assert.Equal(0, await stores.Database.CountAsync("EASYSSF_REVOCATION")));
        await cleanup.StopAsync();
    }

    private static async Task<bool> SeenBeforeAsync(ISsfJtiDedupStore store, SsfEventToken set)
    {
        SsfJtiClaim claim = await store.ClaimAsync(set);
        if (!claim.IsNew)
        {
            return true;
        }
        await store.ProcessedAsync(set, claim);
        return false;
    }

    private static string Normalize(string statement)
    {
        return System.Text.RegularExpressions.Regex.Replace(statement, @"\s+", " ").Trim();
    }

    private static SsfEventToken Set(string jti)
    {
        return Set(Issuer, jti);
    }

    private static SsfEventToken Set(string issuer, string jti)
    {
        return new SsfEventToken(jti, issuer, Now, null, [], null, null, SsfJson.EmptyObject);
    }

    /// <summary>
    /// The stores of one test on a database with the three tables, and the clock they run on.
    /// </summary>
    private sealed class Stores : IAsyncDisposable
    {
        private Stores(TestDatabase database)
        {
            Database = database;
        }

        public TestDatabase Database { get; }

        public TestClock Clock { get; } = new(Now);

        public static async Task<Stores> CreateAsync(string database)
        {
            TestDatabase db = await TestDatabase.OpenAsync(database);
            await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.ProcessedSetTable(Prefix), DbSsfSchema.CreateProcessedSetTable(Prefix), true, null);
            await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.RevocationTable(Prefix), DbSsfSchema.CreateRevocationTable(Prefix), true, null);
            await DbSsfSchema.PrepareTableAsync(db.Db, DbSsfSchema.PollAckTable(Prefix), DbSsfSchema.CreatePollAckTable(Prefix), true, null);
            return new Stores(db);
        }

        public DbSsfPollAckStore AckStore()
        {
            return new DbSsfPollAckStore(Database.Db, Prefix, Clock) { Retention = TimeSpan.FromDays(1) };
        }

        public DbSsfJtiDedupStore DedupStore()
        {
            return new DbSsfJtiDedupStore(Database.Db, Prefix, Clock) { Retention = TimeSpan.FromDays(1) };
        }

        public DbSsfTokenRevocationStore RevocationStore()
        {
            return new DbSsfTokenRevocationStore(Database.Db, TimeSpan.FromMinutes(10), Prefix, Clock);
        }

        public async Task<string> StateAsync(string jti)
        {
            return (await Database.QueryAsync("SELECT STATE FROM EASYSSF_PROCESSED_SET WHERE JTI = ?", reader => reader.GetString(0), jti))[0];
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
