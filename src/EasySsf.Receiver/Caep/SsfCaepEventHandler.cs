using EasySsf.Core.Caep;
using EasySsf.Receiver.Event;

namespace EasySsf.Receiver.Caep;

/// <summary>
/// An <see cref="ISsfEventHandler"/> for CAEP 1.0 events that dispatches every CAEP event of a SET
/// to the method of its kind: <see cref="OnSessionRevokedAsync"/>,
/// <see cref="OnTokenClaimsChangeAsync"/>, <see cref="OnCredentialChangeAsync"/>,
/// <see cref="OnAssuranceLevelChangeAsync"/>, <see cref="OnDeviceComplianceChangeAsync"/>,
/// <see cref="OnSessionEstablishedAsync"/>, <see cref="OnSessionPresentedAsync"/>,
/// <see cref="OnRiskLevelChangeAsync"/>, and <see cref="OnOtherCaepEventAsync"/> for an event type
/// in the CAEP namespace this version does not know. The methods do nothing by default, override
/// the ones the receiver acts on. SETs without CAEP events are ignored.
/// <para>
/// Each event comes with typed access to its payload and with the <see cref="SsfEventContext"/> of
/// the SET, whose <see cref="SsfEventContext.Subject">subject</see> the event is about. The
/// built-in reactions to <c>session-revoked</c> and <c>credential-change</c>, ending sessions and
/// rejecting tokens, are <c>SsfSessionTerminationEventHandler</c> and
/// <c>SsfTokenRevocationEventHandler</c>; this handler is for what the application does on top.
/// Like every handler, an implementation has to be idempotent: a SET is delivered again if any
/// handler failed.
/// </para>
/// <code>
/// class StepUpHandler : SsfCaepEventHandler
/// {
///     protected override Task OnAssuranceLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext,
///             CancellationToken cancellationToken)
///     {
///         return (caepEvent.ChangeDirection == "decrease")
///             ? stepUp.RequireAsync(eventContext.Subject.Subject, caepEvent.CurrentLevel, cancellationToken)
///             : Task.CompletedTask;
///     }
/// }
/// </code>
/// </summary>
public abstract class SsfCaepEventHandler : ISsfEventHandler
{
    public async Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        foreach (SsfCaepEvent caepEvent in SsfCaepEvent.In(eventContext.EventToken))
        {
            await OnCaepEventAsync(caepEvent, eventContext, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called for every CAEP event of a SET, dispatches to the method of its kind. Override to treat
    /// all CAEP events alike.
    /// </summary>
    /// <param name="caepEvent">the event</param>
    /// <param name="eventContext">the SET the event arrived in</param>
    /// <param name="cancellationToken">cancels the handling</param>
    protected virtual Task OnCaepEventAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return caepEvent.Kind switch
        {
            SsfCaepEventKind.SessionRevoked => OnSessionRevokedAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.TokenClaimsChange => OnTokenClaimsChangeAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.CredentialChange => OnCredentialChangeAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.AssuranceLevelChange => OnAssuranceLevelChangeAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.DeviceComplianceChange => OnDeviceComplianceChangeAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.SessionEstablished => OnSessionEstablishedAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.SessionPresented => OnSessionPresentedAsync(caepEvent, eventContext, cancellationToken),
            SsfCaepEventKind.RiskLevelChange => OnRiskLevelChangeAsync(caepEvent, eventContext, cancellationToken),
            _ => OnOtherCaepEventAsync(caepEvent, eventContext, cancellationToken),
        };
    }

    /// <summary>
    /// The session was revoked (<c>CaepSessionRevoked</c>).
    /// </summary>
    protected virtual Task OnSessionRevokedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Claims of the token changed (<c>CaepTokenClaimsChange</c>), see <see cref="SsfCaepEvent.Claims"/>.
    /// </summary>
    protected virtual Task OnTokenClaimsChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A credential was created, revoked, updated or deleted (<c>CaepCredentialChange</c>), see
    /// <see cref="SsfCaepEvent.CredentialType"/> and <see cref="SsfCaepEvent.ChangeType"/>.
    /// </summary>
    protected virtual Task OnCredentialChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The assurance level changed (<c>CaepAssuranceLevelChange</c>), see
    /// <see cref="SsfCaepEvent.CurrentLevel"/> and <see cref="SsfCaepEvent.ChangeDirection"/>.
    /// </summary>
    protected virtual Task OnAssuranceLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The compliance status of the device changed (<c>CaepDeviceComplianceChange</c>), see
    /// <see cref="SsfCaepEvent.CurrentStatus"/>.
    /// </summary>
    protected virtual Task OnDeviceComplianceChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A session was established (<c>CaepSessionEstablished</c>).
    /// </summary>
    protected virtual Task OnSessionEstablishedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A session was presented (<c>CaepSessionPresented</c>).
    /// </summary>
    protected virtual Task OnSessionPresentedAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The risk level changed (<c>CaepRiskLevelChange</c>), see <see cref="SsfCaepEvent.CurrentLevel"/>
    /// and <see cref="SsfCaepEvent.Principal"/>.
    /// </summary>
    protected virtual Task OnRiskLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// An event in the CAEP namespace this version of easyssf does not know
    /// (<see cref="SsfCaepEventKind.Other"/>): the common claims of CAEP section 2 are typed, the
    /// rest is in <see cref="SsfCaepEvent.Payload"/>.
    /// </summary>
    protected virtual Task OnOtherCaepEventAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
