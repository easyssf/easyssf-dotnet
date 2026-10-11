using EasySsf.Receiver.Poll;

namespace EasySsf.Receiver.Data;

/// <summary>
/// <see cref="ISsfPollAckStore"/> that keeps the acknowledgements a poller owes its transmitter in
/// a database table, so that they survive a restart: a SET handled right before the application
/// stopped is acknowledged with the first poll after it started again, instead of being delivered
/// once more.
/// <para>
/// Entries a transmitter never accepts are forgotten after the <see cref="Retention"/> (7 days by
/// default): the store deletes them when it is written to, at most once a minute, and
/// <see cref="PurgeExpiredAsync"/> does so on demand, see <see cref="DbSsfStoreCleanup"/>. The
/// table is checked or created with <see cref="PrepareTableAsync"/>; the constructor does not touch
/// the database.
/// </para>
/// </summary>
/// <seealso cref="DbSsfSchema"/>
public sealed class DbSsfPollAckStore : ISsfPollAckStore, IDbSsfExpiringStore
{
    /// <summary>
    /// How many entries one DELETE removes by default; databases limit the number of parameters of
    /// a statement.
    /// </summary>
    public const int DefaultDeleteBatchSize = 100;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private const int MaxDescriptionLength = 1024;

    private readonly ISsfDbOperations _db;

    private readonly string _tablePrefix;

    private readonly string _insert;

    private readonly string _select;

    private readonly string _deletePrefix;

    private readonly string _count;

    private readonly string _deleteExpired;

    private readonly TimeProvider _timeProvider;

    private TimeSpan _retention = TimeSpan.FromDays(7);

    private int _deleteBatchSize = DefaultDeleteBatchSize;

    private long _nextCleanupTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <param name="db">runs the statements</param>
    /// <param name="tablePrefix">the prefix of the table name, see <see cref="DbSsfSchema.DefaultTablePrefix"/></param>
    /// <param name="timeProvider">the clock, the system clock if <see langword="null"/></param>
    public DbSsfPollAckStore(ISsfDbOperations db, string tablePrefix = DbSsfSchema.DefaultTablePrefix, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        Table = DbSsfSchema.PollAckTable(tablePrefix);
        _db = db;
        _tablePrefix = tablePrefix;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _insert = "INSERT INTO " + Table + " (ISSUER, JTI, ERROR_CODE, ERROR_DESCRIPTION, RECORDED_AT) VALUES (?, ?, ?, ?, ?)";
        // the limit is applied by reading no further, the SQL for it differs between databases
        _select = "SELECT JTI, ERROR_CODE, ERROR_DESCRIPTION FROM " + Table + " WHERE ISSUER = ? ORDER BY RECORDED_AT, JTI";
        _deletePrefix = "DELETE FROM " + Table + " WHERE ISSUER = ? AND JTI IN (";
        _count = "SELECT COUNT(*) FROM " + Table + " WHERE ISSUER = ?";
        _deleteExpired = "DELETE FROM " + Table + " WHERE RECORDED_AT < ?";
    }

    /// <summary>
    /// The name of the table.
    /// </summary>
    public string Table { get; }

    /// <summary>
    /// How long an entry the transmitter does not accept is kept, 7 days by default.
    /// </summary>
    public TimeSpan Retention
    {
        get => _retention;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _retention = value;
        }
    }

    /// <summary>
    /// How many entries one DELETE statement removes (<c>JTI IN (...)</c>), 100 by default.
    /// Databases limit the number of parameters of a statement (SQL Server to 2100, Oracle to 1000
    /// list members); a smaller batch means more statements per poll.
    /// </summary>
    public int DeleteBatchSize
    {
        get => _deleteBatchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _deleteBatchSize = value;
        }
    }

    /// <summary>
    /// Makes sure the table exists, see
    /// <see cref="DbSsfSchema.PrepareTableAsync(ISsfDbOperations, string, IReadOnlyList{string}, bool, string?, CancellationToken)"/>.
    /// </summary>
    /// <param name="create">whether to create a missing table</param>
    /// <param name="hint">what else the application could do, for the message, may be <see langword="null"/></param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <exception cref="InvalidOperationException">if the table is missing and is not to be created</exception>
    public ValueTask PrepareTableAsync(bool create, string? hint = null, CancellationToken cancellationToken = default)
    {
        return DbSsfSchema.PrepareTableAsync(_db, Table, DbSsfSchema.CreatePollAckTable(_tablePrefix), create, hint, cancellationToken);
    }

    public async ValueTask RecordAsync(string issuer, SsfPendingAck ack, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(ack);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await RemoveExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        string? description = ack.ErrorDescription;
        if (description is { Length: > MaxDescriptionLength })
        {
            description = description[..MaxDescriptionLength];
        }
        try
        {
            await _db.UpdateAsync(_insert, [issuer, ack.Jti, ack.ErrorCode, description, now.ToUnixTimeMilliseconds()], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SsfDbDuplicateKeyException)
        {
            // recorded already, for example a SET delivered twice before it was acknowledged
        }
    }

    public ValueTask<IReadOnlyList<SsfPendingAck>> PendingAsync(string issuer, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        return _db.QueryAsync(_select, limit,
            reader => new SsfPendingAck(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
            [issuer], cancellationToken);
    }

    public async ValueTask RemoveAsync(string issuer, IEnumerable<string> jtis, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(jtis);
        List<string> remaining = jtis.ToList();
        for (int from = 0; from < remaining.Count; from += DeleteBatchSize)
        {
            List<string> chunk = remaining.GetRange(from, Math.Min(DeleteBatchSize, remaining.Count - from));
            object?[] args = new object?[chunk.Count + 1];
            args[0] = issuer;
            for (int i = 0; i < chunk.Count; i++)
            {
                args[i + 1] = chunk[i];
            }
            string placeholders = string.Join(", ", Enumerable.Repeat("?", chunk.Count));
            await _db.UpdateAsync(_deletePrefix + placeholders + ")", args, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<int> CountAsync(string issuer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        IReadOnlyList<long> count = await _db.QueryAsync(_count, reader => DbSsfJtiDedupStore.Int64(reader, 0), [issuer], cancellationToken)
            .ConfigureAwait(false);
        return (int)count[0];
    }

    public ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Volatile.Write(ref _nextCleanupTicks, (now + CleanupInterval).UtcTicks);
        return _db.UpdateAsync(_deleteExpired, [(now - Retention).ToUnixTimeMilliseconds()], cancellationToken);
    }

    private async ValueTask RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now.UtcTicks > Volatile.Read(ref _nextCleanupTicks))
        {
            await PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
