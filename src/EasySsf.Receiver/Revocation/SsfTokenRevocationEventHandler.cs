using EasySsf.Core.Event;
using EasySsf.Receiver.Event;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Revocation;

/// <summary>
/// Records the session or subject of revocation events (by default CAEP <c>session-revoked</c>) in
/// the <see cref="ISsfTokenRevocationStore"/>.
/// <para>
/// An event that names a session revokes the tokens of that session. An event that only names a
/// user revokes all tokens of that user issued up to the time of the event. User event types (for
/// example CAEP <c>credential-change</c>) revoke the user whether or not the event names a session.
/// </para>
/// </summary>
public sealed class SsfTokenRevocationEventHandler : ISsfEventHandler
{
    private readonly ISsfTokenRevocationStore _revocationStore;

    private readonly IReadOnlyList<string> _sessionEventTypes;

    private readonly IReadOnlyList<string> _userEventTypes;

    private readonly ILogger _logger;

    /// <param name="revocationStore">records the revocations</param>
    /// <param name="eventTypes">the event types (alias or URI) that revoke the session they name, or the user if they name none</param>
    /// <param name="userEventTypes">the event types (alias or URI) that revoke the user they name, session or not; none if <see langword="null"/></param>
    /// <param name="logger">logs the revocations, nothing if <see langword="null"/></param>
    public SsfTokenRevocationEventHandler(ISsfTokenRevocationStore revocationStore, IEnumerable<string> eventTypes,
            IEnumerable<string>? userEventTypes = null, ILogger<SsfTokenRevocationEventHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(revocationStore);
        ArgumentNullException.ThrowIfNull(eventTypes);
        _revocationStore = revocationStore;
        _sessionEventTypes = eventTypes.Select(SsfEventTypes.Resolve).ToList().AsReadOnly();
        _userEventTypes = (userEventTypes ?? []).Select(SsfEventTypes.Resolve).ToList().AsReadOnly();
        _logger = logger ?? NullLogger<SsfTokenRevocationEventHandler>.Instance;
    }

    public async Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        foreach (string eventType in _sessionEventTypes)
        {
            if (eventContext.HasEvent(eventType))
            {
                await RevokeAsync(eventContext, eventType, eventContext.SubjectFor(eventType), cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (string eventType in _userEventTypes)
        {
            if (eventContext.HasEvent(eventType))
            {
                await RevokeAsync(eventContext, eventType, eventContext.SubjectFor(eventType).WithoutSession(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RevokeAsync(SsfEventContext eventContext, string eventType, SsfSubject subject, CancellationToken cancellationToken)
    {
        DateTimeOffset revokedAt = eventContext.EventTimestamp(eventType);
        string alias = SsfEventTypes.AliasOf(eventType);
        string transmitter = eventContext.EventToken.Iss;
        if (subject.SessionId is { } sessionId)
        {
            string issuer = subject.Session!.Issuer ?? IssuerOf(subject.User, transmitter);
            await _revocationStore.RevokeSessionAsync(issuer, sessionId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Event}: revoked access tokens of session {SessionId} at {Issuer}", alias, sessionId, issuer);
        }
        else if (subject.Subject is { } userSubject)
        {
            string issuer = IssuerOf(subject.UserIdentifier, transmitter);
            await _revocationStore.RevokeSubjectAsync(issuer, userSubject, revokedAt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Event}: revoked access tokens of subject {Subject} at {Issuer}", alias, userSubject, issuer);
        }
        else if (subject.OpaqueId is { } opaqueId)
        {
            // an opaque identifier does not tell whether it names a session or a user
            await _revocationStore.RevokeSessionAsync(transmitter, opaqueId, cancellationToken).ConfigureAwait(false);
            await _revocationStore.RevokeSubjectAsync(transmitter, opaqueId, revokedAt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Event}: revoked access tokens of session or subject {Id} at {Issuer}", alias, opaqueId, transmitter);
        }
        else
        {
            _logger.LogWarning("{Event} event of SET {Jti} has no subject that identifies a session or user by sid or sub, no tokens were revoked",
                alias, eventContext.EventToken.Jti);
        }
    }

    /// <summary>
    /// The issuer of the tokens an identifier refers to: the one of an <c>iss_sub</c> identifier,
    /// else the transmitter that sent the event. For a session, the issuer of the user it belongs to
    /// counts as well.
    /// </summary>
    private static string IssuerOf(SsfSubjectIdentifier? identifier, string transmitter)
    {
        return identifier?.Issuer ?? transmitter;
    }
}
