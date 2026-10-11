namespace EasySsf.Receiver.Revocation;

/// <summary>
/// Remembers which sessions and subjects were revoked, so that their access tokens and
/// authentication cookies can be rejected. Implement it on top of a shared store to share
/// revocations between the instances of an application, since a SET is pushed to only one of them.
/// <para>
/// Sessions and subjects are scoped to the issuer of their tokens: a subject <c>alice</c> of one
/// issuer is not the subject <c>alice</c> of another. Events name that issuer in an <c>iss_sub</c>
/// identifier; for identifiers without one, the transmitter that sent the event is taken to be the
/// issuer.
/// </para>
/// <para>
/// Revocations only need to be kept for as long as a token issued before the revocation can still
/// be valid, i.e. for the maximum token or session lifetime.
/// </para>
/// </summary>
public interface ISsfTokenRevocationStore
{
    /// <summary>
    /// Revokes all tokens bound to the given session.
    /// </summary>
    /// <param name="issuer">the issuer of the tokens</param>
    /// <param name="sessionId">the session identifier, as found in the <c>sid</c> claim</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask RevokeSessionAsync(string issuer, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes all tokens of the given subject that were issued at or before the given time.
    /// </summary>
    /// <param name="issuer">the issuer of the tokens</param>
    /// <param name="subject">the subject, as found in the <c>sub</c> claim</param>
    /// <param name="revokedAt">the time of the revocation</param>
    /// <param name="cancellationToken">cancels the call</param>
    ValueTask RevokeSubjectAsync(string issuer, string subject, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    ValueTask<bool> IsSessionRevokedAsync(string issuer, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The time up to which the tokens of the given subject are revoked.
    /// </summary>
    /// <returns>the time of the most recent revocation, or <see langword="null"/> if the subject is not revoked</returns>
    ValueTask<DateTimeOffset?> GetSubjectRevokedAtAsync(string issuer, string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a token was revoked: it is bound to a revoked session, or it belongs to a revoked
    /// subject and was issued at or before the revocation.
    /// </summary>
    /// <param name="issuer">the <c>iss</c> claim of the token; a token without one is not revoked</param>
    /// <param name="sessionId">the <c>sid</c> claim of the token, may be <see langword="null"/></param>
    /// <param name="subject">the <c>sub</c> claim of the token, may be <see langword="null"/></param>
    /// <param name="issuedAt">the <c>iat</c> claim of the token, may be <see langword="null"/></param>
    /// <param name="cancellationToken">cancels the call</param>
    async ValueTask<bool> IsRevokedAsync(string? issuer, string? sessionId, string? subject, DateTimeOffset? issuedAt,
            CancellationToken cancellationToken = default)
    {
        if (issuer is null)
        {
            return false;
        }
        if (sessionId is not null && await IsSessionRevokedAsync(issuer, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (subject is null)
        {
            return false;
        }
        DateTimeOffset? revokedAt = await GetSubjectRevokedAtAsync(issuer, subject, cancellationToken).ConfigureAwait(false);
        return revokedAt is not null && (issuedAt is null || issuedAt <= revokedAt);
    }
}
