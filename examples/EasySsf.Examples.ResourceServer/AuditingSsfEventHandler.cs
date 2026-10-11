using EasySsf.Core.Event;
using EasySsf.Receiver.Event;

namespace EasySsf.Examples.ResourceServer;

/// <summary>
/// An application specific handler. It is invoked for every verified SET, in addition to the
/// handler of the receiver that revokes the access tokens.
/// </summary>
public sealed class AuditingSsfEventHandler(ILogger<AuditingSsfEventHandler> logger) : ISsfEventHandler
{
    public Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        foreach (string eventType in eventContext.EventTypes)
        {
            SsfSubject subject = eventContext.SubjectFor(eventType);
            logger.LogInformation("Security event {Event} for user {Subject} and session {Session}: {Payload}", SsfEventTypes.AliasOf(eventType),
                subject.Subject, subject.SessionId, eventContext.EventFor(eventType));
        }
        return Task.CompletedTask;
    }
}
