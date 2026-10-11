using System.Collections.Concurrent;

namespace EasySsf.Receiver.Poll;

/// <summary>
/// <see cref="ISsfPollAckStore"/> that keeps the pending acknowledgements in memory, in the order
/// they were recorded. Lost when the application stops, the default of the poller.
/// </summary>
public sealed class InMemorySsfPollAckStore : ISsfPollAckStore
{
    private readonly ConcurrentDictionary<string, Acks> _pending = new(StringComparer.Ordinal);

    public ValueTask RecordAsync(string issuer, SsfPendingAck ack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ack);
        Acks acks = AcksOf(issuer);
        lock (acks)
        {
            acks.ByJti.TryAdd(ack.Jti, ack);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SsfPendingAck>> PendingAsync(string issuer, int limit, CancellationToken cancellationToken = default)
    {
        Acks acks = AcksOf(issuer);
        lock (acks)
        {
            return ValueTask.FromResult<IReadOnlyList<SsfPendingAck>>(acks.ByJti.Values.Take(Math.Max(0, limit)).ToList().AsReadOnly());
        }
    }

    public ValueTask RemoveAsync(string issuer, IEnumerable<string> jtis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jtis);
        Acks acks = AcksOf(issuer);
        lock (acks)
        {
            foreach (string jti in jtis)
            {
                acks.ByJti.Remove(jti);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CountAsync(string issuer, CancellationToken cancellationToken = default)
    {
        Acks acks = AcksOf(issuer);
        lock (acks)
        {
            return ValueTask.FromResult(acks.ByJti.Count);
        }
    }

    private Acks AcksOf(string? issuer)
    {
        return _pending.GetOrAdd(issuer ?? "null", _ => new Acks());
    }

    /// <summary>
    /// The pending acknowledgements of one transmitter, in order.
    /// </summary>
    private sealed class Acks
    {
        public OrderedDictionary<string, SsfPendingAck> ByJti { get; } = new(StringComparer.Ordinal);
    }
}
