using EasySsf.Core.Risc;
using EasySsf.Receiver.Event;

namespace EasySsf.Receiver.Risc;

/// <summary>
/// An <see cref="ISsfEventHandler"/> for RISC 1.0 events that dispatches every RISC event of a SET
/// to the method of its kind: <see cref="OnAccountCredentialChangeRequiredAsync"/>,
/// <see cref="OnAccountPurgedAsync"/>, <see cref="OnAccountDisabledAsync"/>,
/// <see cref="OnAccountEnabledAsync"/>, <see cref="OnIdentifierChangedAsync"/>,
/// <see cref="OnIdentifierRecycledAsync"/>, <see cref="OnCredentialCompromiseAsync"/>,
/// <see cref="OnOptInAsync"/>, <see cref="OnOptOutInitiatedAsync"/>,
/// <see cref="OnOptOutCancelledAsync"/>, <see cref="OnOptOutEffectiveAsync"/>,
/// <see cref="OnRecoveryActivatedAsync"/>, <see cref="OnRecoveryInformationChangedAsync"/>,
/// <see cref="OnSessionsRevokedAsync"/>, and <see cref="OnOtherRiscEventAsync"/> for an event type
/// in the RISC namespace this version does not know. The methods do nothing by default, override
/// the ones the receiver acts on. SETs without RISC events are ignored.
/// <para>
/// Each event comes with the <see cref="SsfEventContext"/> of the SET, whose
/// <see cref="SsfEventContext.Subject">subject</see> is the account the event is about. Like every
/// handler, an implementation has to be idempotent: a SET is delivered again if any handler failed.
/// </para>
/// <code>
/// class AccountHandler : SsfRiscEventHandler
/// {
///     protected override Task OnAccountDisabledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext,
///             CancellationToken cancellationToken)
///     {
///         return accounts.SuspendAsync(eventContext.Subject.Email, riscEvent.Reason, cancellationToken);
///     }
///
///     protected override Task OnAccountPurgedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext,
///             CancellationToken cancellationToken)
///     {
///         return accounts.DeleteAsync(eventContext.Subject.Email, cancellationToken);
///     }
/// }
/// </code>
/// </summary>
public abstract class SsfRiscEventHandler : ISsfEventHandler
{
    public async Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        foreach (SsfRiscEvent riscEvent in SsfRiscEvent.In(eventContext.EventToken))
        {
            await OnRiscEventAsync(riscEvent, eventContext, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called for every RISC event of a SET, dispatches to the method of its kind. Override to treat
    /// all RISC events alike.
    /// </summary>
    /// <param name="riscEvent">the event</param>
    /// <param name="eventContext">the SET the event arrived in</param>
    /// <param name="cancellationToken">cancels the handling</param>
    protected virtual Task OnRiscEventAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return riscEvent.Kind switch
        {
            SsfRiscEventKind.AccountCredentialChangeRequired => OnAccountCredentialChangeRequiredAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.AccountPurged => OnAccountPurgedAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.AccountDisabled => OnAccountDisabledAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.AccountEnabled => OnAccountEnabledAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.IdentifierChanged => OnIdentifierChangedAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.IdentifierRecycled => OnIdentifierRecycledAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.CredentialCompromise => OnCredentialCompromiseAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.OptIn => OnOptInAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.OptOutInitiated => OnOptOutInitiatedAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.OptOutCancelled => OnOptOutCancelledAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.OptOutEffective => OnOptOutEffectiveAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.RecoveryActivated => OnRecoveryActivatedAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.RecoveryInformationChanged => OnRecoveryInformationChangedAsync(riscEvent, eventContext, cancellationToken),
            SsfRiscEventKind.SessionsRevoked => OnSessionsRevokedAsync(riscEvent, eventContext, cancellationToken),
            _ => OnOtherRiscEventAsync(riscEvent, eventContext, cancellationToken),
        };
    }

    /// <summary>
    /// The account has to change its credential (<c>RiscAccountCredentialChangeRequired</c>).
    /// </summary>
    protected virtual Task OnAccountCredentialChangeRequiredAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account was permanently deleted (<c>RiscAccountPurged</c>).
    /// </summary>
    protected virtual Task OnAccountPurgedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account was disabled (<c>RiscAccountDisabled</c>), see <see cref="SsfRiscEvent.Reason"/>.
    /// </summary>
    protected virtual Task OnAccountDisabledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account was enabled again (<c>RiscAccountEnabled</c>).
    /// </summary>
    protected virtual Task OnAccountEnabledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The identifier of the account changed (<c>RiscIdentifierChanged</c>), see
    /// <see cref="SsfRiscEvent.NewValue"/>.
    /// </summary>
    protected virtual Task OnIdentifierChangedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The identifier was given to someone else (<c>RiscIdentifierRecycled</c>).
    /// </summary>
    protected virtual Task OnIdentifierRecycledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A credential was found compromised (<c>RiscCredentialCompromise</c>), see
    /// <see cref="SsfRiscEvent.CredentialType"/>.
    /// </summary>
    protected virtual Task OnCredentialCompromiseAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account takes part in the RISC exchange (<c>RiscOptIn</c>).
    /// </summary>
    protected virtual Task OnOptInAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The user asked to leave the RISC exchange (<c>RiscOptOutInitiated</c>).
    /// </summary>
    protected virtual Task OnOptOutInitiatedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The user withdrew the request to leave (<c>RiscOptOutCancelled</c>).
    /// </summary>
    protected virtual Task OnOptOutCancelledAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account left the RISC exchange (<c>RiscOptOutEffective</c>).
    /// </summary>
    protected virtual Task OnOptOutEffectiveAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The account went through a recovery flow (<c>RiscRecoveryActivated</c>).
    /// </summary>
    protected virtual Task OnRecoveryActivatedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The recovery information changed (<c>RiscRecoveryInformationChanged</c>).
    /// </summary>
    protected virtual Task OnRecoveryInformationChangedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// All sessions of the account were revoked (<c>RiscSessionsRevoked</c>, deprecated by RISC in
    /// favour of <c>CaepSessionRevoked</c>).
    /// </summary>
    protected virtual Task OnSessionsRevokedAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// An event in the RISC namespace this version of easyssf does not know
    /// (<see cref="SsfRiscEventKind.Other"/>); its attributes are in <see cref="SsfRiscEvent.Payload"/>.
    /// </summary>
    protected virtual Task OnOtherRiscEventAsync(SsfRiscEvent riscEvent, SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
