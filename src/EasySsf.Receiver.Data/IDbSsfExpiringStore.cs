namespace EasySsf.Receiver.Data;

/// <summary>
/// A store whose rows expire and are purged, by the store itself when it is written to and
/// periodically by <see cref="DbSsfStoreCleanup"/>.
/// </summary>
public interface IDbSsfExpiringStore
{
    /// <summary>
    /// Deletes the rows that expired.
    /// </summary>
    /// <returns>the number of rows deleted</returns>
    ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
