using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Data;

/// <summary>
/// Purges the expired rows of the <see cref="IDbSsfExpiringStore">stores</see> periodically, so
/// that they do not linger in a receiver that gets no events for a while. Either call
/// <see cref="PurgeExpiredAsync"/> from the scheduler of the application, or <see cref="Start"/> to
/// run it on a background task of its own.
/// </summary>
public sealed class DbSsfStoreCleanup : IDisposable
{
    private readonly IReadOnlyList<IDbSsfExpiringStore> _stores;

    private readonly ILogger _logger;

    private readonly TimeProvider _timeProvider;

    private readonly Lock _lock = new();

    private CancellationTokenSource? _stopping;

    private Task? _loop;

    /// <param name="stores">the stores to purge</param>
    /// <param name="interval">how often; the periodic cleanup is off when zero or negative</param>
    /// <param name="logger">logs failures, none if <see langword="null"/></param>
    /// <param name="timeProvider">the clock of the timer, the system clock if <see langword="null"/></param>
    public DbSsfStoreCleanup(IEnumerable<IDbSsfExpiringStore> stores, TimeSpan interval, ILogger<DbSsfStoreCleanup>? logger = null,
            TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _stores = stores.ToArray();
        Interval = interval;
        _logger = logger ?? NullLogger<DbSsfStoreCleanup>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How often the stores are purged.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Whether the periodic cleanup runs when started: the interval is positive and there is a
    /// store to purge.
    /// </summary>
    public bool IsEnabled => Interval > TimeSpan.Zero && _stores.Count > 0;

    /// <summary>
    /// Whether the periodic cleanup is running.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _loop) is not null;

    /// <summary>
    /// Purges the expired rows of all stores once. A store that fails is logged and skipped.
    /// </summary>
    /// <returns>the number of rows deleted</returns>
    public async ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        int deleted = 0;
        foreach (IDbSsfExpiringStore store in _stores)
        {
            try
            {
                deleted += await store.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not purge the expired rows of {Store}", store.GetType().Name);
            }
        }
        if (deleted > 0)
        {
            _logger.LogDebug("Purged {Count} expired rows", deleted);
        }
        return deleted;
    }

    /// <summary>
    /// Starts purging periodically in the background, if <see cref="IsEnabled">enabled</see>.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null || !IsEnabled)
            {
                return;
            }
            _stopping = new CancellationTokenSource();
            _loop = RunAsync(_stopping.Token);
        }
    }

    /// <summary>
    /// Stops the periodic cleanup, waiting for a purge in progress.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        CancellationTokenSource? stopping;
        lock (_lock)
        {
            loop = _loop;
            stopping = _stopping;
            _loop = null;
            _stopping = null;
        }
        if (loop is null || stopping is null)
        {
            return;
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped or stopping was cancelled
        }
        stopping.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        using PeriodicTimer timer = new(Interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                await PurgeExpiredAsync(stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }
}
