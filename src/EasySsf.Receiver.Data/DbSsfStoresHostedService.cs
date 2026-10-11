using EasySsf.Receiver.Hosting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Data;

/// <summary>
/// Prepares the tables of the database stores before the receiver starts (the receiver's own
/// hosted service claims SETs as soon as it polls), and purges their expired rows every
/// <c>Data:CleanupInterval</c> while the host runs. Does nothing when the receiver or the stores
/// are disabled.
/// </summary>
public sealed class DbSsfStoresHostedService(IServiceProvider services, IOptions<SsfReceiverOptions> options,
        ILogger<DbSsfStoresHostedService> logger) : IHostedLifecycleService
{
    private DbSsfStoreCleanup? _cleanup;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        SsfReceiverOptions receiver = options.Value;
        if (!receiver.Enabled || !receiver.Data.Enabled)
        {
            return;
        }
        DbSsfStores stores = services.GetService(typeof(DbSsfStores)) as DbSsfStores
            ?? throw new InvalidOperationException("DbSsfStores is not registered, call AddDatabaseStores()");
        bool create = SsfReceiverDataExtensions.CreateTables(receiver.Data, stores.Db);
        List<IDbSsfExpiringStore> expiring = [];
        if (receiver.Dedup.Enabled)
        {
            await stores.DedupStore.PrepareTableAsync(create, SsfReceiverDataExtensions.Hint, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Processed SETs are remembered in the table {Table}", stores.DedupStore.Table);
            expiring.Add(stores.DedupStore);
        }
        if (receiver.Revocation.Enabled)
        {
            await stores.RevocationStore.PrepareTableAsync(create, SsfReceiverDataExtensions.Hint, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Revoked sessions and subjects are kept in the table {Table}", stores.RevocationStore.Table);
            expiring.Add(stores.RevocationStore);
        }
        await stores.AckStore.PrepareTableAsync(create, SsfReceiverDataExtensions.Hint, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Pending poll acknowledgements are kept in the table {Table}", stores.AckStore.Table);
        expiring.Add(stores.AckStore);
        _cleanup = new DbSsfStoreCleanup(expiring, receiver.Data.CleanupInterval,
            services.GetService(typeof(ILogger<DbSsfStoreCleanup>)) as ILogger<DbSsfStoreCleanup>);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cleanup is { IsEnabled: true })
        {
            _cleanup.Start();
            logger.LogInformation("Expired rows of the easyssf tables are purged every {Interval}", _cleanup.Interval);
        }
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cleanup is not null)
        {
            await _cleanup.StopAsync(cancellationToken).ConfigureAwait(false);
            _cleanup.Dispose();
            _cleanup = null;
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
