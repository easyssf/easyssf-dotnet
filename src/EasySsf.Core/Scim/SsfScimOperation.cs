using EasySsf.Core.Event;

namespace EasySsf.Core.Scim;

/// <summary>
/// What a SCIM Event (RFC 9967) reports, independent of whether its payload carries the resource
/// (<c>full</c>) or only names the attributes that changed (<c>notice</c>).
/// </summary>
public enum SsfScimOperation
{
    /// <summary>
    /// The resource joined the event feed (section 2.3.1), it is not necessarily new.
    /// </summary>
    FeedAdd,

    /// <summary>
    /// The resource left the event feed (section 2.3.2), it was not necessarily deleted.
    /// </summary>
    FeedRemove,

    /// <summary>
    /// The resource was created (section 2.4.1).
    /// </summary>
    Create,

    /// <summary>
    /// The resource was modified with SCIM PATCH (section 2.4.2).
    /// </summary>
    Patch,

    /// <summary>
    /// The resource was replaced with SCIM PUT (section 2.4.3).
    /// </summary>
    Put,

    /// <summary>
    /// The resource was deleted, and thereby removed from the feed (section 2.4.4).
    /// </summary>
    Delete,

    /// <summary>
    /// The resource was activated, for example its account may be logged in to (section 2.4.5).
    /// </summary>
    Activate,

    /// <summary>
    /// The resource was deactivated, typically its user may no longer have an active session
    /// (section 2.4.6).
    /// </summary>
    Deactivate,

    /// <summary>
    /// An asynchronous SCIM request completed (section 2.5.1.3).
    /// </summary>
    AsyncResponse,
}

/// <summary>
/// The event type URIs of the <see cref="SsfScimOperation"/>s.
/// </summary>
public static class SsfScimOperations
{
    private static readonly IReadOnlyDictionary<string, SsfScimOperation> ByEventType = new Dictionary<string, SsfScimOperation>(StringComparer.Ordinal)
    {
        [SsfEventTypes.ScimFeedAdd] = SsfScimOperation.FeedAdd,
        [SsfEventTypes.ScimFeedRemove] = SsfScimOperation.FeedRemove,
        [SsfEventTypes.ScimProvCreateNotice] = SsfScimOperation.Create,
        [SsfEventTypes.ScimProvCreateFull] = SsfScimOperation.Create,
        [SsfEventTypes.ScimProvPatchNotice] = SsfScimOperation.Patch,
        [SsfEventTypes.ScimProvPatchFull] = SsfScimOperation.Patch,
        [SsfEventTypes.ScimProvPutNotice] = SsfScimOperation.Put,
        [SsfEventTypes.ScimProvPutFull] = SsfScimOperation.Put,
        [SsfEventTypes.ScimProvDelete] = SsfScimOperation.Delete,
        [SsfEventTypes.ScimProvActivate] = SsfScimOperation.Activate,
        [SsfEventTypes.ScimProvDeactivate] = SsfScimOperation.Deactivate,
        [SsfEventTypes.ScimMiscAsyncResponse] = SsfScimOperation.AsyncResponse,
    };

    /// <summary>
    /// The event type URIs that report the operation, one or, for the provisioning operations with
    /// a <c>notice</c> and a <c>full</c> variant, two.
    /// </summary>
    public static IReadOnlyList<string> EventTypes(this SsfScimOperation operation)
    {
        return operation switch
        {
            SsfScimOperation.FeedAdd => [SsfEventTypes.ScimFeedAdd],
            SsfScimOperation.FeedRemove => [SsfEventTypes.ScimFeedRemove],
            SsfScimOperation.Create => [SsfEventTypes.ScimProvCreateNotice, SsfEventTypes.ScimProvCreateFull],
            SsfScimOperation.Patch => [SsfEventTypes.ScimProvPatchNotice, SsfEventTypes.ScimProvPatchFull],
            SsfScimOperation.Put => [SsfEventTypes.ScimProvPutNotice, SsfEventTypes.ScimProvPutFull],
            SsfScimOperation.Delete => [SsfEventTypes.ScimProvDelete],
            SsfScimOperation.Activate => [SsfEventTypes.ScimProvActivate],
            SsfScimOperation.Deactivate => [SsfEventTypes.ScimProvDeactivate],
            SsfScimOperation.AsyncResponse => [SsfEventTypes.ScimMiscAsyncResponse],
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown SCIM operation"),
        };
    }

    /// <summary>
    /// Whether this is one of the provisioning operations of section 2.4, which report a change of
    /// the resource at the SCIM service provider.
    /// </summary>
    public static bool IsProvisioning(this SsfScimOperation operation)
    {
        return operation is not (SsfScimOperation.FeedAdd or SsfScimOperation.FeedRemove or SsfScimOperation.AsyncResponse);
    }

    /// <param name="eventType">an event type URI or alias</param>
    /// <returns>the operation the event type reports, <see langword="null"/> if it is not a SCIM Event</returns>
    public static SsfScimOperation? Of(string? eventType)
    {
        return (eventType is not null && ByEventType.TryGetValue(SsfEventTypes.Resolve(eventType), out SsfScimOperation operation))
            ? operation : null;
    }

    /// <summary>
    /// Whether the event type is one of SCIM Events.
    /// </summary>
    public static bool IsScimEvent(string? eventType)
    {
        return Of(eventType) is not null;
    }
}
