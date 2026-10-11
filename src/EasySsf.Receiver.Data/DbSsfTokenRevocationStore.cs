using EasySsf.Receiver.Revocation;

namespace EasySsf.Receiver.Data;

/// <summary>
/// <see cref="ISsfTokenRevocationStore"/> that keeps revocations in a database table, so that all
/// instances of an application reject the access tokens and cookies of a revoked session or
/// subject, whichever instance received the event. Sessions and subjects are keyed by the issuer of
/// their tokens.
/// <para>
/// Checking a token takes one query on the primary key of the table. Revocations are removed after
/// the time to live: the store deletes them when it is written to, and
/// <see cref="PurgeExpiredAsync"/> does so on demand, see <see cref="DbSsfStoreCleanup"/>. The
/// table is checked or created with <see cref="PrepareTableAsync"/>; the constructor does not touch
/// the database.
/// </para>
/// </summary>
/// <seealso cref="DbSsfSchema"/>
public sealed class DbSsfTokenRevocationStore : ISsfTokenRevocationStore, IDbSsfExpiringStore
{
    private const string Session = "SESSION";

    private const string Subject = "SUBJECT";

    private readonly ISsfDbOperations _db;

    private readonly string _tablePrefix;

    private readonly TimeSpan _ttl;

    private readonly TimeProvider _timeProvider;

    /// <param name="db">runs the statements</param>
    /// <param name="ttl">
    /// how long a revocation is kept, at least the maximum token or cookie lifetime
    /// </param>
    /// <param name="tablePrefix">the prefix of the table name, see <see cref="DbSsfSchema.DefaultTablePrefix"/></param>
    /// <param name="timeProvider">the clock, the system clock if <see langword="null"/></param>
    public DbSsfTokenRevocationStore(ISsfDbOperations db, TimeSpan ttl, string tablePrefix = DbSsfSchema.DefaultTablePrefix,
            TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        _db = db;
        _ttl = ttl;
        _tablePrefix = tablePrefix;
        Table = DbSsfSchema.RevocationTable(tablePrefix);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The name of the table.
    /// </summary>
    public string Table { get; }

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
        return DbSsfSchema.PrepareTableAsync(_db, Table, DbSsfSchema.CreateRevocationTable(_tablePrefix), create, hint, cancellationToken);
    }

    public ValueTask RevokeSessionAsync(string issuer, string sessionId, CancellationToken cancellationToken = default)
    {
        return RevokeAsync(Session, issuer, sessionId, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public ValueTask RevokeSubjectAsync(string issuer, string subject, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        return RevokeAsync(Subject, issuer, subject, revokedAt, cancellationToken);
    }

    public ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        return _db.UpdateAsync("DELETE FROM " + Table + " WHERE EXPIRES_AT <= ?", [_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()], cancellationToken);
    }

    public ValueTask<bool> IsSessionRevokedAsync(string issuer, string sessionId, CancellationToken cancellationToken = default)
    {
        return IsRevokedAsync(issuer, sessionId, null, null, cancellationToken);
    }

    public async ValueTask<DateTimeOffset?> GetSubjectRevokedAtAsync(string issuer, string subject, CancellationToken cancellationToken = default)
    {
        if (issuer is null || subject is null)
        {
            return null;
        }
        IReadOnlyList<long> revokedAt = await _db.QueryAsync(
            "SELECT REVOKED_AT FROM " + Table + " WHERE KIND = ? AND ISSUER = ? AND ID = ? AND EXPIRES_AT > ?",
            reader => DbSsfJtiDedupStore.Int64(reader, 0), [Subject, issuer, subject, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()],
            cancellationToken).ConfigureAwait(false);
        return (revokedAt.Count == 0) ? null : DateTimeOffset.FromUnixTimeMilliseconds(revokedAt[0]);
    }

    /// <summary>
    /// Whether a token was revoked, with one query for the session and the subject.
    /// </summary>
    public async ValueTask<bool> IsRevokedAsync(string? issuer, string? sessionId, string? subject, DateTimeOffset? issuedAt,
            CancellationToken cancellationToken = default)
    {
        if (issuer is null || (sessionId is null && subject is null))
        {
            return false;
        }
        List<string> conditions = [];
        List<object?> arguments = [_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), issuer];
        if (sessionId is not null)
        {
            conditions.Add("(KIND = ? AND ID = ?)");
            arguments.Add(Session);
            arguments.Add(sessionId);
        }
        if (subject is not null)
        {
            conditions.Add("(KIND = ? AND ID = ?)");
            arguments.Add(Subject);
            arguments.Add(subject);
        }
        string query = "SELECT KIND, REVOKED_AT FROM " + Table + " WHERE EXPIRES_AT > ? AND ISSUER = ? AND (" + string.Join(" OR ", conditions) + ")";
        long? issuedAtMillis = issuedAt?.ToUnixTimeMilliseconds();
        IReadOnlyList<bool> revoked = await _db.QueryAsync(query,
            reader => reader.GetString(0) == Session || issuedAtMillis is null || issuedAtMillis <= DbSsfJtiDedupStore.Int64(reader, 1),
            arguments.ToArray(), cancellationToken).ConfigureAwait(false);
        return revoked.Contains(true);
    }

    private async ValueTask RevokeAsync(string kind, string issuer, string id, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        long revokedAtMillis = revokedAt.ToUnixTimeMilliseconds();
        long expiresAt = (now + _ttl).ToUnixTimeMilliseconds();
        if (await UpdateAsync(kind, issuer, id, revokedAtMillis, expiresAt, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await _db.UpdateAsync("INSERT INTO " + Table + " (KIND, ISSUER, ID, REVOKED_AT, EXPIRES_AT) VALUES (?, ?, ?, ?, ?)",
                [kind, issuer, id, revokedAtMillis, expiresAt], cancellationToken).ConfigureAwait(false);
        }
        catch (SsfDbDuplicateKeyException)
        {
            // revoked by another instance at the same time
            await UpdateAsync(kind, issuer, id, revokedAtMillis, expiresAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> UpdateAsync(string kind, string issuer, string id, long revokedAt, long expiresAt, CancellationToken cancellationToken)
    {
        int updated = await _db.UpdateAsync(
            "UPDATE " + Table + " SET REVOKED_AT = ?, EXPIRES_AT = ? WHERE KIND = ? AND ISSUER = ? AND ID = ? AND REVOKED_AT <= ?",
            [revokedAt, expiresAt, kind, issuer, id, revokedAt], cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            // an event delivered late must not shorten a more recent revocation
            updated = await _db.UpdateAsync("UPDATE " + Table + " SET EXPIRES_AT = ? WHERE KIND = ? AND ISSUER = ? AND ID = ?",
                [expiresAt, kind, issuer, id], cancellationToken).ConfigureAwait(false);
        }
        return updated > 0;
    }
}
