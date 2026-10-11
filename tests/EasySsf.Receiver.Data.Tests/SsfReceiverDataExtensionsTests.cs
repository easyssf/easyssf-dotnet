using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Revocation;
using EasySsf.Receiver.Set;
using EasySsf.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// The database stores in a host: added with <c>AddDatabaseStores()</c>, configured at
/// <c>EasySsf:Receiver:Data</c>, tables prepared before the receiver starts.
/// </summary>
public sealed class SsfReceiverDataExtensionsTests : IClassFixture<TestTransmitter>
{
    private readonly TestTransmitter _transmitter;

    public SsfReceiverDataExtensionsTests(TestTransmitter transmitter)
    {
        _transmitter = transmitter;
        _transmitter.Reset();
    }

    [Fact]
    public async Task DatabaseStoresReplaceTheInMemoryOnesAndTablesAreCreatedInAnEmbeddedDatabase()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter, ("EasySsf:Receiver:Dedup:Lease", "00:00:42")), _transmitter)
            .AddDatabaseStores(_ => db.Db);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<DbSsfJtiDedupStore>(provider.GetRequiredService<ISsfJtiDedupStore>());
        Assert.IsType<DbSsfTokenRevocationStore>(provider.GetRequiredService<ISsfTokenRevocationStore>());
        Assert.IsType<DbSsfPollAckStore>(provider.GetRequiredService<ISsfPollAckStore>());
        Assert.Equal(TimeSpan.FromSeconds(42), provider.GetRequiredService<DbSsfStores>().DedupStore.Lease);
        Assert.False(await db.Db.TableExistsAsync("EASYSSF_PROCESSED_SET"));

        await StartAsync(provider);
        Assert.True(await db.Db.TableExistsAsync("EASYSSF_PROCESSED_SET"));
        Assert.True(await db.Db.TableExistsAsync("EASYSSF_REVOCATION"));
        Assert.True(await db.Db.TableExistsAsync("EASYSSF_POLL_ACK"));
        // the receiver works on the tables
        ISsfTokenRevocationStore revocations = provider.GetRequiredService<ISsfTokenRevocationStore>();
        await revocations.RevokeSessionAsync(_transmitter.Issuer, "s1");
        Assert.Equal(1, await db.CountAsync("EASYSSF_REVOCATION"));
        await StopAsync(provider);
    }

    [Fact]
    public async Task TablesAreOnlyCreatedWhereConfiguredAndAMissingTableFailsTheStartWithTheStatements()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter, ("EasySsf:Receiver:Data:InitializeSchema", "Never"),
            ("EasySsf:Receiver:Data:TablePrefix", "APP_")), _transmitter).AddDatabaseStores(_ => db.Db);
        await using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));
        Assert.Contains("APP_PROCESSED_SET", ex.Message);
        Assert.Contains("schema.sql", ex.Message);
        Assert.Contains("Data:InitializeSchema=Always", ex.Message);
        Assert.False(await db.Db.TableExistsAsync("APP_PROCESSED_SET"));

        // the application created the tables: nothing to do
        foreach (string statement in DbSsfSchema.Statements(DbSsfSchema.SchemaSql))
        {
            await db.ExecuteAsync(statement.Replace("EASYSSF_", "APP_"));
        }
        await StartAsync(provider);
        await StopAsync(provider);
    }

    [Fact]
    public async Task DisabledStoresLeaveTheStateInMemory()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter, ("EasySsf:Receiver:Data:Enabled", "false")), _transmitter)
            .AddDatabaseStores(_ => db.Db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<InMemorySsfJtiDedupStore>(provider.GetRequiredService<ISsfJtiDedupStore>());
        Assert.IsType<InMemorySsfTokenRevocationStore>(provider.GetRequiredService<ISsfTokenRevocationStore>());
        Assert.IsType<InMemorySsfPollAckStore>(provider.GetRequiredService<ISsfPollAckStore>());
        await StartAsync(provider);
        Assert.False(await db.Db.TableExistsAsync("EASYSSF_PROCESSED_SET"));
        await StopAsync(provider);
    }

    [Fact]
    public async Task StoresTheApplicationRegisteredAreKept()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        InMemorySsfJtiDedupStore own = new(10);
        services.AddSingleton<ISsfJtiDedupStore>(own);
        services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter).AddDatabaseStores(_ => db.Db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(own, provider.GetRequiredService<ISsfJtiDedupStore>());
        Assert.IsType<DbSsfTokenRevocationStore>(provider.GetRequiredService<ISsfTokenRevocationStore>());
        Assert.Single(provider.GetServices<ISsfJtiDedupStore>());
    }

    [Fact]
    public async Task AddingTheStoresTwiceAddsNothingTwice()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        SsfReceiverBuilder builder = services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter);
        builder.AddDatabaseStores(_ => db.Db);
        builder.AddDatabaseStores(_ => db.Db);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DbSsfStores));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(DbSsfStoresHostedService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISsfPollAckStore));
    }

    [Fact]
    public async Task ExpiredRowsArePurgedPeriodically()
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(TestDatabase.Sqlite);
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter, ("EasySsf:Receiver:Data:CleanupInterval", "00:00:00.050"),
            ("EasySsf:Receiver:Data:AckRetention", "00:00:00.010")), _transmitter).AddDatabaseStores(_ => db.Db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await StartAsync(provider);
        // an acknowledgement nobody collects expires and is purged by the cleanup, not by a write
        await db.UpdateAsync("INSERT INTO EASYSSF_POLL_ACK (ISSUER, JTI, RECORDED_AT) VALUES (?, ?, ?)", _transmitter.Issuer, "stale", 0L);
        await Eventually.UntilAssertedAsync(async () => Assert.Equal(0, await db.CountAsync("EASYSSF_POLL_ACK")));
        await StopAsync(provider);
    }

    [Fact]
    public void InvalidDataOptionsAreReported()
    {
        SsfReceiverOptions options = new() { TransmitterIssuer = _transmitter.Issuer };
        options.Data.TablePrefix = "X; DROP";
        options.Data.AckDeleteBatchSize = 0;
        SsfReceiverConfigurationException ex = Assert.Throws<SsfReceiverConfigurationException>(() => SsfReceiverOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("Data:TablePrefix", ex.Message);
        Assert.Contains("AckDeleteBatchSize", ex.Message);
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (IHostedLifecycleService service in provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>())
        {
            await service.StartingAsync(CancellationToken.None);
        }
        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    private static async Task StopAsync(ServiceProvider provider)
    {
        foreach (IHostedService service in provider.GetServices<IHostedService>().Reverse())
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
