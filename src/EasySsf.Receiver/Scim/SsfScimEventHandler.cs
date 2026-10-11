using EasySsf.Core.Scim;
using EasySsf.Receiver.Event;

namespace EasySsf.Receiver.Scim;

/// <summary>
/// An <see cref="ISsfEventHandler"/> for SCIM Events (RFC 9967) that dispatches every SCIM Event
/// of a SET to the method of its operation: <see cref="OnCreateAsync"/>, <see cref="OnPatchAsync"/>,
/// <see cref="OnPutAsync"/>, <see cref="OnDeleteAsync"/>, <see cref="OnActivateAsync"/>,
/// <see cref="OnDeactivateAsync"/>, <see cref="OnFeedAddAsync"/>, <see cref="OnFeedRemoveAsync"/>
/// and <see cref="OnAsyncResponseAsync"/>. The methods do nothing by default, override the ones
/// the receiver acts on. SETs without SCIM Events are ignored.
/// <para>
/// Each event comes with its <see cref="SsfScimEvent.Subject">subject</see>, the SCIM resource it
/// is about, and the <see cref="SsfEventContext"/> of the SET for its <c>jti</c>, <c>iss</c> and
/// <c>txn</c>. Like every handler, an implementation has to be idempotent: a SET is delivered
/// again if any handler failed. The <c>txn</c> claim identifies the underlying transaction across
/// such retransmissions.
/// </para>
/// <code>
/// class ProvisioningHandler : SsfScimEventHandler
/// {
///     protected override Task OnCreateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext,
///             CancellationToken cancellationToken)
///     {
///         return (scimEvent.IsFull &amp;&amp; scimEvent.Subject?.ResourceType == "Users")
///             ? users.CreateAsync(scimEvent.Subject.Id, scimEvent.Data, cancellationToken)
///             : Task.CompletedTask;
///     }
///
///     protected override Task OnDeactivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext,
///             CancellationToken cancellationToken)
///     {
///         return users.DisableAsync(scimEvent.Subject?.Id, cancellationToken);
///     }
/// }
/// </code>
/// </summary>
public abstract class SsfScimEventHandler : ISsfEventHandler
{
    public async Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        foreach (SsfScimEvent scimEvent in SsfScimEvent.In(eventContext.EventToken))
        {
            await OnScimEventAsync(scimEvent, eventContext, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called for every SCIM Event of a SET, dispatches to the method of its operation. Override to
    /// treat all SCIM Events alike, or to refuse events without a <see cref="SsfScimSubject">SCIM
    /// subject</see>.
    /// </summary>
    /// <param name="scimEvent">the event</param>
    /// <param name="eventContext">the SET the event arrived in</param>
    /// <param name="cancellationToken">cancels the handling</param>
    protected virtual Task OnScimEventAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return scimEvent.Operation switch
        {
            SsfScimOperation.FeedAdd => OnFeedAddAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.FeedRemove => OnFeedRemoveAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Create => OnCreateAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Patch => OnPatchAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Put => OnPutAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Delete => OnDeleteAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Activate => OnActivateAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.Deactivate => OnDeactivateAsync(scimEvent, eventContext, cancellationToken),
            SsfScimOperation.AsyncResponse => OnAsyncResponseAsync(scimEvent, eventContext, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>
    /// The resource joined the event feed (<c>feed:add</c>).
    /// </summary>
    protected virtual Task OnFeedAddAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource left the event feed (<c>feed:remove</c>).
    /// </summary>
    protected virtual Task OnFeedRemoveAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was created (<c>prov:create:notice</c> or <c>prov:create:full</c>).
    /// </summary>
    protected virtual Task OnCreateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was modified with SCIM PATCH (<c>prov:patch:notice</c> or <c>prov:patch:full</c>).
    /// </summary>
    protected virtual Task OnPatchAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was replaced with SCIM PUT (<c>prov:put:notice</c> or <c>prov:put:full</c>).
    /// </summary>
    protected virtual Task OnPutAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was deleted (<c>prov:delete</c>).
    /// </summary>
    protected virtual Task OnDeleteAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was activated (<c>prov:activate</c>).
    /// </summary>
    protected virtual Task OnActivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The resource was deactivated (<c>prov:deactivate</c>).
    /// </summary>
    protected virtual Task OnDeactivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// An asynchronous SCIM request completed (<c>misc:asyncresp</c>).
    /// </summary>
    protected virtual Task OnAsyncResponseAsync(SsfScimEvent scimEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
