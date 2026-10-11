using EasySsf.Core.Event;

namespace EasySsf.Receiver.Session;

/// <summary>
/// Terminates the local sessions of a subject. Implement it for sessions the application keeps
/// server-side, for example in a ticket store or a distributed cache; stateless authentication
/// cookies are rejected on their next request through the <c>ISsfTokenRevocationStore</c> instead.
/// </summary>
public interface ISsfSessionTerminator
{
    /// <summary>
    /// Terminates the sessions that belong to the given subject: the session with the subject's
    /// session identifier if it has one, otherwise all sessions of its user.
    /// </summary>
    /// <returns>the number of terminated sessions</returns>
    ValueTask<int> TerminateSessionsAsync(SsfSubject subject, CancellationToken cancellationToken = default);
}
