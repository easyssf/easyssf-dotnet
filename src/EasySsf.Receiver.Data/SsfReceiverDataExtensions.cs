using System.Data.Common;

using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Revocation;
using EasySsf.Receiver.Set;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Data;

/// <summary>
/// Keeps the state of the receiver in the database of the application: which SETs were processed,
/// which sessions and subjects were revoked, and which acknowledgements a poller owes. All instances
/// of the application then share that state. Configured at <c>EasySsf:Receiver:Data</c>
/// (<see cref="SsfDataOptions"/>).
/// </summary>
public static class SsfReceiverDataExtensions
{
    private const string SchemaHint = "set " + SsfReceiverDefaults.ConfigurationSection + ":Data:InitializeSchema=Always to have it created on startup, or set "
        + SsfReceiverDefaults.ConfigurationSection + ":Data:Enabled=false to keep the state of the receiver in memory";

    /// <summary>
    /// Adds the database stores on the <see cref="DbDataSource"/> registered with the services, as
    /// <c>AddNpgsqlDataSource()</c> or <c>AddMySqlDataSource()</c> do.
    /// </summary>
    public static SsfReceiverBuilder AddDatabaseStores(this SsfReceiverBuilder builder)
    {
        return builder.AddDatabaseStores(provider => new DataSourceSsfDbOperations(provider.GetRequiredService<DbDataSource>()));
    }

    /// <summary>
    /// Adds the database stores on the given data source.
    /// </summary>
    public static SsfReceiverBuilder AddDatabaseStores(this SsfReceiverBuilder builder, DbDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return builder.AddDatabaseStores(_ => new DataSourceSsfDbOperations(dataSource));
    }

    /// <summary>
    /// Adds the database stores on connections from the given factory, for a provider without a
    /// <see cref="DbDataSource"/>, for example <c>() => new SqliteConnection(connectionString)</c>.
    /// </summary>
    public static SsfReceiverBuilder AddDatabaseStores(this SsfReceiverBuilder builder, Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return builder.AddDatabaseStores(_ => new DataSourceSsfDbOperations(connectionFactory));
    }

    /// <summary>
    /// Adds the database stores on the given operations: the <see cref="DbSsfJtiDedupStore"/>, the
    /// <see cref="DbSsfTokenRevocationStore"/> and the <see cref="DbSsfPollAckStore"/> replace the
    /// in-memory stores unless the application registered a store of its own, the tables are checked
    /// or created when the host starts (<c>Data:InitializeSchema</c>), and the expired rows are
    /// purged every <c>Data:CleanupInterval</c>. With <c>Data:Enabled=false</c> the in-memory stores
    /// stay.
    /// </summary>
    /// <param name="builder">the receiver</param>
    /// <param name="operations">
    /// creates the <see cref="ISsfDbOperations"/>, a <see cref="DataSourceSsfDbOperations"/> or an
    /// implementation over the application's own data access
    /// </param>
    public static SsfReceiverBuilder AddDatabaseStores(this SsfReceiverBuilder builder, Func<IServiceProvider, ISsfDbOperations> operations)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(operations);
        IServiceCollection services = builder.Services;
        if (services.Any(descriptor => descriptor.ServiceType == typeof(DbSsfStores)))
        {
            return builder;
        }
        services.AddSingleton(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            SsfDataOptions data = options.Data;
            ISsfDbOperations db = operations(provider);
            DbSsfJtiDedupStore dedupStore = new(db, data.TablePrefix) { Retention = options.Dedup.Retention, Lease = options.Dedup.Lease };
            DbSsfTokenRevocationStore revocationStore = new(db, options.Revocation.Ttl, data.TablePrefix);
            DbSsfPollAckStore ackStore = new(db, data.TablePrefix) { Retention = data.AckRetention, DeleteBatchSize = data.AckDeleteBatchSize };
            return new DbSsfStores(db, dedupStore, revocationStore, ackStore);
        });
        builder.ReplaceDefaultStore<ISsfJtiDedupStore>(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            return options.Data.Enabled
                ? provider.GetRequiredService<DbSsfStores>().DedupStore
                : new InMemorySsfJtiDedupStore(options.Dedup.Capacity, options.Dedup.Lease);
        });
        builder.ReplaceDefaultStore<ISsfTokenRevocationStore>(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            return options.Data.Enabled
                ? provider.GetRequiredService<DbSsfStores>().RevocationStore
                : new InMemorySsfTokenRevocationStore(options.Revocation.Ttl);
        });
        builder.ReplaceDefaultStore<ISsfPollAckStore>(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            return options.Data.Enabled ? provider.GetRequiredService<DbSsfStores>().AckStore : new InMemorySsfPollAckStore();
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DbSsfStoresHostedService>());
        return builder;
    }

    /// <summary>
    /// Whether the tables are to be created, by <c>Data:InitializeSchema</c>.
    /// </summary>
    internal static bool CreateTables(SsfDataOptions data, ISsfDbOperations db)
    {
        return data.InitializeSchema switch
        {
            SsfSchemaInitialization.Always => true,
            SsfSchemaInitialization.Never => false,
            _ => db is DataSourceSsfDbOperations dataSource && dataSource.IsEmbeddedDatabase(),
        };
    }

    internal static string Hint => SchemaHint;
}

/// <summary>
/// The database stores of the receiver, built from the options.
/// </summary>
public sealed class DbSsfStores
{
    internal DbSsfStores(ISsfDbOperations db, DbSsfJtiDedupStore dedupStore, DbSsfTokenRevocationStore revocationStore, DbSsfPollAckStore ackStore)
    {
        Db = db;
        DedupStore = dedupStore;
        RevocationStore = revocationStore;
        AckStore = ackStore;
    }

    /// <summary>
    /// What the stores run their statements with.
    /// </summary>
    public ISsfDbOperations Db { get; }

    public DbSsfJtiDedupStore DedupStore { get; }

    public DbSsfTokenRevocationStore RevocationStore { get; }

    public DbSsfPollAckStore AckStore { get; }
}
