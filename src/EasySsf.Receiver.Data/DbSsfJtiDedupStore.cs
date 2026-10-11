using System.Data.Common;
using System.Globalization;

using EasySsf.Core.Event;
using EasySsf.Receiver.Set;

namespace EasySsf.Receiver.Data;

/// <summary>
/// <see cref="ISsfJtiDedupStore"/> that remembers processed SETs in a database table shared by the
/// instances of an application: a SET delivered to several of them or again after a restart is
/// handled by one and skipped by the others, and a SET one instance is handling is left alone by
/// the rest. Processing stays at least once, see <see cref="ISsfJtiDedupStore"/>.
/// <para>
/// A claim inserts the row with the state <c>IN_PROGRESS</c>: the primary key of the table makes
/// that atomic, and a second instance that meets the row leaves the SET alone. The handlers'
/// success turns the state into <c>PROCESSED</c>. <c>STATE_CHANGED_AT</c> is the time of the last
/// state change, so an <c>IN_PROGRESS</c> row older than the <see cref="Lease"/> (60 seconds by
/// default) counts as abandoned by a crashed instance and the next claim takes it over. That
/// <c>STATE_CHANGED_AT</c> is also the token of the claim: the success or failure of the handlers
/// only changes the row while it still carries the token they were granted, so an instance that
/// outlived the lease leaves the claim of the taker-over alone. A <see cref="RenewAsync">renewal</see>
/// moves <c>STATE_CHANGED_AT</c> forward the same way a take-over does, fenced by the current token,
/// and the new value is the token of the renewed claim.
/// </para>
/// <para>
/// SETs are forgotten after the <see cref="Retention"/>: the store deletes them when it is written
/// to, at most once a minute, and <see cref="PurgeExpiredAsync"/> does so on demand, see
/// <see cref="DbSsfStoreCleanup"/>. The table is checked or created with
/// <see cref="PrepareTableAsync"/>; the constructor does not touch the database.
/// </para>
/// </summary>
/// <seealso cref="DbSsfSchema"/>
public sealed class DbSsfJtiDedupStore : ISsfJtiDedupStore, IDbSsfExpiringStore
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private const string InProgress = "IN_PROGRESS";

    private const string Processed = "PROCESSED";

    private readonly ISsfDbOperations _db;

    private readonly string _tablePrefix;

    private readonly string _insert;

    private readonly string _select;

    private readonly string _moveClaim;

    private readonly string _markProcessed;

    private readonly string _delete;

    private readonly string _deleteExpired;

    private readonly TimeProvider _timeProvider;

    private TimeSpan _retention = TimeSpan.FromDays(7);

    private TimeSpan _lease = InMemorySsfJtiDedupStore.DefaultLease;

    private long _nextCleanupTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <param name="db">runs the statements</param>
    /// <param name="tablePrefix">the prefix of the table name, see <see cref="DbSsfSchema.DefaultTablePrefix"/></param>
    /// <param name="timeProvider">the clock, the system clock if <see langword="null"/></param>
    public DbSsfJtiDedupStore(ISsfDbOperations db, string tablePrefix = DbSsfSchema.DefaultTablePrefix, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        Table = DbSsfSchema.ProcessedSetTable(tablePrefix);
        _db = db;
        _tablePrefix = tablePrefix;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _insert = "INSERT INTO " + Table + " (ISSUER, JTI, STATE, STATE_CHANGED_AT) VALUES (?, ?, ?, ?)";
        _select = "SELECT STATE, STATE_CHANGED_AT FROM " + Table + " WHERE ISSUER = ? AND JTI = ?";
        // a take-over and a renewal both move the claim's token forward, fenced by the current one
        _moveClaim = "UPDATE " + Table + " SET STATE_CHANGED_AT = ? WHERE ISSUER = ? AND JTI = ? AND STATE = '" + InProgress
            + "' AND STATE_CHANGED_AT = ?";
        // processed and forget are fenced by the claim's token, the STATE_CHANGED_AT of the claim
        _markProcessed = "UPDATE " + Table + " SET STATE = '" + Processed + "', STATE_CHANGED_AT = ? WHERE ISSUER = ? AND JTI = ? AND STATE = '"
            + InProgress + "' AND STATE_CHANGED_AT = ?";
        _delete = "DELETE FROM " + Table + " WHERE ISSUER = ? AND JTI = ? AND STATE = '" + InProgress + "' AND STATE_CHANGED_AT = ?";
        _deleteExpired = "DELETE FROM " + Table + " WHERE STATE_CHANGED_AT < ?";
    }

    /// <summary>
    /// The name of the table.
    /// </summary>
    public string Table { get; }

    /// <summary>
    /// How long a processed SET is remembered, 7 days by default. Has to cover the time the
    /// transmitter keeps trying to deliver a SET.
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
    /// How long a claim holds before the SET counts as abandoned by a crashed instance and is
    /// handled again; 60 seconds by default, longer than the longest handler.
    /// </summary>
    public TimeSpan Lease
    {
        get => _lease;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _lease = value;
        }
    }

    /// <summary>
    /// Makes sure the table exists and has the columns of this release, see
    /// <see cref="DbSsfSchema.PrepareTableAsync(ISsfDbOperations, string, IReadOnlyList{string}, IReadOnlyList{DbSsfSchemaUpgrade}, bool, string?, CancellationToken)"/>.
    /// </summary>
    /// <param name="create">whether to create a missing table and to add missing columns</param>
    /// <param name="hint">what else the application could do, for the message, may be <see langword="null"/></param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <exception cref="InvalidOperationException">if the table is missing or outdated and is not to be changed</exception>
    public ValueTask PrepareTableAsync(bool create, string? hint = null, CancellationToken cancellationToken = default)
    {
        return DbSsfSchema.PrepareTableAsync(_db, Table, DbSsfSchema.CreateProcessedSetTable(_tablePrefix),
            DbSsfSchema.ProcessedSetUpgrades(_tablePrefix), create, hint, cancellationToken);
    }

    public async ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await RemoveExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        long token = now.ToUnixTimeMilliseconds();
        try
        {
            await _db.UpdateAsync(_insert, [eventToken.Iss, eventToken.Jti, InProgress, token], cancellationToken).ConfigureAwait(false);
            return SsfJtiClaim.Granted(token);
        }
        catch (SsfDbDuplicateKeyException)
        {
            IReadOnlyList<Row> rows = await SelectAsync(eventToken, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                // deleted in between: the redelivery will be claimed
                return SsfJtiClaim.InProgress;
            }
            Row row = rows[0];
            if (row.State == Processed)
            {
                return SsfJtiClaim.Processed;
            }
            DateTimeOffset since = DateTimeOffset.FromUnixTimeMilliseconds(row.Since);
            if (since + Lease <= now)
            {
                // abandoned: take it over, unless another instance did just now. The new
                // STATE_CHANGED_AT is the token of the new claim; the old holder's token no longer
                // matches, so it can neither complete nor forget this claim.
                int taken = await _db.UpdateAsync(_moveClaim, [token, eventToken.Iss, eventToken.Jti, row.Since], cancellationToken).ConfigureAwait(false);
                return (taken == 1) ? SsfJtiClaim.Granted(token) : SsfJtiClaim.InProgress;
            }
            return SsfJtiClaim.InProgress;
        }
    }

    public async ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        ArgumentNullException.ThrowIfNull(claim);
        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        int updated = await _db.UpdateAsync(_markProcessed, [now, eventToken.Iss, eventToken.Jti, claim.Token], cancellationToken).ConfigureAwait(false);
        if (updated == 0 && (await SelectAsync(eventToken, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            // the claim was purged in between: record the success anyway. A duplicate means another
            // instance claimed the SET since, which then owns it.
            try
            {
                await _db.UpdateAsync(_insert, [eventToken.Iss, eventToken.Jti, Processed, now], cancellationToken).ConfigureAwait(false);
            }
            catch (SsfDbDuplicateKeyException)
            {
                // taken over meanwhile
            }
        }
        // otherwise the claim was taken over after the lease: the SET belongs to the new holder
    }

    public async ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        ArgumentNullException.ThrowIfNull(claim);
        await _db.UpdateAsync(_delete, [eventToken.Iss, eventToken.Jti, claim.Token], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SsfJtiClaim?> RenewAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventToken);
        ArgumentNullException.ThrowIfNull(claim);
        // the new STATE_CHANGED_AT is the token of the renewed claim: later than the old one even
        // within the same millisecond, so that the row is seen as changed
        long token = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), claim.Token + 1);
        int renewed = await _db.UpdateAsync(_moveClaim, [token, eventToken.Iss, eventToken.Jti, claim.Token], cancellationToken).ConfigureAwait(false);
        return (renewed == 1) ? SsfJtiClaim.Granted(token) : null;
    }

    public ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Volatile.Write(ref _nextCleanupTicks, (now + CleanupInterval).UtcTicks);
        return _db.UpdateAsync(_deleteExpired, [(now - Retention).ToUnixTimeMilliseconds()], cancellationToken);
    }

    private ValueTask<IReadOnlyList<Row>> SelectAsync(SsfEventToken eventToken, CancellationToken cancellationToken)
    {
        return _db.QueryAsync(_select, reader => new Row(reader.GetString(0), Int64(reader, 1)), [eventToken.Iss, eventToken.Jti], cancellationToken);
    }

    private async ValueTask RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now.UtcTicks > Volatile.Read(ref _nextCleanupTicks))
        {
            await PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a BIGINT column, whatever integer type the provider returns it as.
    /// </summary>
    internal static long Int64(DbDataReader reader, int ordinal)
    {
        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private sealed record Row(string State, long Since);
}
