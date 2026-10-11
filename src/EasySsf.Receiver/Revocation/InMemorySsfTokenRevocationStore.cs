using System.Collections.Concurrent;

namespace EasySsf.Receiver.Revocation;

/// <summary>
/// <see cref="ISsfTokenRevocationStore"/> that keeps revocations in memory for a fixed time. Its
/// content is lost on restart and not shared between instances.
/// </summary>
public sealed class InMemorySsfTokenRevocationStore : ISsfTokenRevocationStore
{
    private readonly ConcurrentDictionary<(string Issuer, string Id), DateTimeOffset> _sessionExpirations = new();

    private readonly ConcurrentDictionary<(string Issuer, string Id), SubjectRevocation> _subjectRevocations = new();

    private readonly TimeSpan _ttl;

    private readonly TimeProvider _timeProvider;

    /// <param name="ttl">how long a revocation is remembered, at least the maximum token lifetime</param>
    /// <param name="timeProvider">the clock, the system clock if <see langword="null"/></param>
    public InMemorySsfTokenRevocationStore(TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "ttl must be positive");
        }
        _ttl = ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How long a revocation is remembered.
    /// </summary>
    public TimeSpan Ttl => _ttl;

    public ValueTask RevokeSessionAsync(string issuer, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        RemoveExpired(now);
        _sessionExpirations[(issuer, sessionId)] = now + _ttl;
        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeSubjectAsync(string issuer, string subject, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        RemoveExpired(now);
        SubjectRevocation revocation = new(revokedAt, now + _ttl);
        // an event delivered late must not shorten a more recent revocation
        _subjectRevocations.AddOrUpdate((issuer, subject), revocation,
            (_, existing) => (existing.RevokedAt > revocation.RevokedAt) ? new SubjectRevocation(existing.RevokedAt, revocation.ExpiresAt) : revocation);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsSessionRevokedAsync(string issuer, string sessionId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_sessionExpirations.TryGetValue((issuer, sessionId), out DateTimeOffset expiresAt)
            && expiresAt > _timeProvider.GetUtcNow());
    }

    public ValueTask<DateTimeOffset?> GetSubjectRevokedAtAsync(string issuer, string subject, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? revokedAt = (_subjectRevocations.TryGetValue((issuer, subject), out SubjectRevocation? revocation)
            && revocation.ExpiresAt > _timeProvider.GetUtcNow()) ? revocation.RevokedAt : null;
        return ValueTask.FromResult(revokedAt);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach ((string Issuer, string Id) key in _sessionExpirations.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
        {
            _sessionExpirations.TryRemove(key, out _);
        }
        foreach ((string Issuer, string Id) key in _subjectRevocations.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
        {
            _subjectRevocations.TryRemove(key, out _);
        }
    }

    private sealed record SubjectRevocation(DateTimeOffset RevokedAt, DateTimeOffset ExpiresAt);
}
