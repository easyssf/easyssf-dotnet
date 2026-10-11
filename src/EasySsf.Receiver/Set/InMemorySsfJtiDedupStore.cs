using EasySsf.Core.Event;

namespace EasySsf.Receiver.Set;

/// <summary>
/// <see cref="ISsfJtiDedupStore"/> that remembers a bounded number of SETs in memory, evicting the
/// oldest first. Its content is lost on restart and not shared between instances, so the
/// <see cref="SsfJtiClaimState.InProgress">in progress</see> state only matters when the same SET
/// arrives twice while a handler of this instance still runs.
/// </summary>
public sealed class InMemorySsfJtiDedupStore : ISsfJtiDedupStore
{
    /// <summary>
    /// How long a claim holds by default: 60 seconds, longer than the longest handler.
    /// </summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(60);

    private readonly OrderedDictionary<string, Known> _entries = new(StringComparer.Ordinal);

    private readonly Lock _lock = new();

    private readonly int _maxEntries;

    private readonly TimeProvider _timeProvider;

    private long _nextToken;

    /// <param name="capacity">how many SETs are remembered, at least one</param>
    /// <param name="lease">
    /// how long a claim holds before the SET counts as abandoned by a crashed or stuck handler and
    /// is handled again; <see cref="DefaultLease"/> if <see langword="null"/>
    /// </param>
    /// <param name="timeProvider">the clock, the system clock if <see langword="null"/></param>
    public InMemorySsfJtiDedupStore(int capacity, TimeSpan? lease = null, TimeProvider? timeProvider = null)
    {
        if (lease is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "lease must be positive");
        }
        _maxEntries = Math.Max(1, capacity);
        Lease = lease ?? DefaultLease;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How long a claim holds before the SET counts as abandoned.
    /// </summary>
    public TimeSpan Lease { get; }

    /// <summary>
    /// The number of SETs remembered, processed or in progress.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public ValueTask<SsfJtiClaim> ClaimAsync(SsfEventToken eventToken, CancellationToken cancellationToken = default)
    {
        string key = ISsfJtiDedupStore.Key(eventToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Known? entry))
            {
                return ValueTask.FromResult(Grant(key, now));
            }
            if (entry.State == SsfJtiClaimState.Processed)
            {
                return ValueTask.FromResult(SsfJtiClaim.Processed);
            }
            if (entry.Since + Lease <= now)
            {
                // abandoned: taken over, with a token of its own
                return ValueTask.FromResult(Grant(key, now));
            }
            return ValueTask.FromResult(SsfJtiClaim.InProgress);
        }
    }

    private SsfJtiClaim Grant(string key, DateTimeOffset now)
    {
        long token = ++_nextToken;
        Put(key, new Known(SsfJtiClaimState.InProgress, now, token));
        return SsfJtiClaim.Granted(token);
    }

    private void Put(string key, Known entry)
    {
        // an existing key keeps its position, like Java's LinkedHashMap
        _entries[key] = entry;
        while (_entries.Count > _maxEntries)
        {
            _entries.RemoveAt(0);
        }
    }

    public ValueTask ProcessedAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        string key = ISsfJtiDedupStore.Key(eventToken);
        lock (_lock)
        {
            // the claim may have been evicted meanwhile: record the success anyway
            if (!_entries.TryGetValue(key, out Known? entry) || Holds(entry, claim))
            {
                Put(key, new Known(SsfJtiClaimState.Processed, _timeProvider.GetUtcNow(), claim.Token));
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ForgetAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        string key = ISsfJtiDedupStore.Key(eventToken);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Known? entry) && Holds(entry, claim))
            {
                _entries.Remove(key);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<SsfJtiClaim?> RenewAsync(SsfEventToken eventToken, SsfJtiClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        string key = ISsfJtiDedupStore.Key(eventToken);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Known? entry) && !Holds(entry, claim))
            {
                return ValueTask.FromResult<SsfJtiClaim?>(null);
            }
            // an evicted claim is restored, as ProcessedAsync records it as well
            Put(key, new Known(SsfJtiClaimState.InProgress, _timeProvider.GetUtcNow(), claim.Token));
            return ValueTask.FromResult<SsfJtiClaim?>(claim);
        }
    }

    /// <summary>
    /// Whether the entry is the claim given: in progress and not taken over since.
    /// </summary>
    private static bool Holds(Known entry, SsfJtiClaim claim)
    {
        return entry.State == SsfJtiClaimState.InProgress && entry.Token == claim.Token;
    }

    private sealed record Known(SsfJtiClaimState State, DateTimeOffset Since, long Token);
}
