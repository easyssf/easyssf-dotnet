using EasySsf.Core.Event;

namespace EasySsf.Receiver.Event;

/// <summary>
/// Creates <see cref="ISsfEventHandler"/>s from delegates.
/// </summary>
public static class SsfEventHandler
{
    /// <summary>
    /// A handler that invokes the delegate for every SET.
    /// </summary>
    public static ISsfEventHandler Of(Func<SsfEventContext, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new DelegateHandler(handler, null);
    }

    /// <summary>
    /// A handler that invokes the delegate for every SET that contains an event of the given type.
    /// </summary>
    /// <param name="aliasOrUri">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <param name="handler">the handler</param>
    public static ISsfEventHandler For(string aliasOrUri, Func<SsfEventContext, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasOrUri);
        ArgumentNullException.ThrowIfNull(handler);
        return new DelegateHandler(handler, SsfEventTypes.Resolve(aliasOrUri));
    }

    private sealed class DelegateHandler(Func<SsfEventContext, CancellationToken, Task> handler, string? eventType)
        : ISsfEventHandler
    {
        public Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
        {
            if (eventType is not null && !eventContext.HasEvent(eventType))
            {
                return Task.CompletedTask;
            }
            return handler(eventContext, cancellationToken);
        }

        public override string ToString()
        {
            return (eventType is null) ? "SsfEventHandler.Of" : "SsfEventHandler.For(" + SsfEventTypes.AliasOf(eventType) + ")";
        }
    }
}
