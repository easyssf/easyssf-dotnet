using EasySsf.Core.Event;
using EasySsf.Receiver.Event;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Session;

/// <summary>
/// Terminates local sessions in reaction to security events.
/// <para>
/// Session events (by default CAEP <c>session-revoked</c>) terminate the session named by the event,
/// or all sessions of its user if it names no session. User events (by default CAEP
/// <c>credential-change</c>) always terminate all sessions of the user.
/// </para>
/// </summary>
public sealed class SsfSessionTerminationEventHandler : ISsfEventHandler
{
    private readonly ISsfSessionTerminator _sessionTerminator;

    private readonly IReadOnlyList<string> _sessionEventTypes;

    private readonly IReadOnlyList<string> _userEventTypes;

    private readonly ILogger _logger;

    /// <param name="sessionTerminator">terminates the sessions</param>
    /// <param name="sessionEventTypes">the event types (alias or URI) that terminate the sessions of their subject</param>
    /// <param name="userEventTypes">the event types (alias or URI) that terminate all sessions of their subject's user</param>
    /// <param name="logger">logs the terminations, nothing if <see langword="null"/></param>
    public SsfSessionTerminationEventHandler(ISsfSessionTerminator sessionTerminator, IEnumerable<string> sessionEventTypes,
            IEnumerable<string> userEventTypes, ILogger<SsfSessionTerminationEventHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessionTerminator);
        ArgumentNullException.ThrowIfNull(sessionEventTypes);
        ArgumentNullException.ThrowIfNull(userEventTypes);
        _sessionTerminator = sessionTerminator;
        _sessionEventTypes = sessionEventTypes.Select(SsfEventTypes.Resolve).ToList().AsReadOnly();
        _userEventTypes = userEventTypes.Select(SsfEventTypes.Resolve).ToList().AsReadOnly();
        _logger = logger ?? NullLogger<SsfSessionTerminationEventHandler>.Instance;
    }

    public async Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        foreach (string eventType in _sessionEventTypes)
        {
            if (eventContext.HasEvent(eventType))
            {
                await TerminateAsync(eventContext, eventType, eventContext.SubjectFor(eventType), cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (string eventType in _userEventTypes)
        {
            if (eventContext.HasEvent(eventType))
            {
                await TerminateAsync(eventContext, eventType, eventContext.SubjectFor(eventType).WithoutSession(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TerminateAsync(SsfEventContext eventContext, string eventType, SsfSubject subject, CancellationToken cancellationToken)
    {
        string alias = SsfEventTypes.AliasOf(eventType);
        if (subject.IsEmpty)
        {
            _logger.LogWarning("{Event} event of SET {Jti} has no subject that identifies a session or user, no sessions were terminated",
                alias, eventContext.EventToken.Jti);
            return;
        }
        int terminated = await _sessionTerminator.TerminateSessionsAsync(subject, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Event}: terminated {Count} session(s) of {Subject}", alias, terminated, Describe(subject));
    }

    private static string Describe(SsfSubject subject)
    {
        if (subject.SessionId is { } sessionId)
        {
            return "session " + sessionId;
        }
        SsfSubjectIdentifier? user = subject.UserIdentifier;
        if (user is not null)
        {
            return "user " + (user.Value ?? user.Format);
        }
        return "session or subject " + subject.OpaqueId;
    }
}
