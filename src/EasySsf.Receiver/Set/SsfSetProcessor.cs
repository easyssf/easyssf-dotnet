using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Set;

/// <summary>
/// Processes a received SET independent of how it was delivered: verifies it, skips it if it was
/// processed before and hands it to the <see cref="ISsfEventHandler">handlers</see>.
/// </summary>
public sealed class SsfSetProcessor
{
    private readonly ISsfSetVerifier _verifier;

    private readonly ISsfJtiDedupStore? _dedupStore;

    private readonly IReadOnlyList<ISsfEventHandler> _handlers;

    private readonly SsfSetProcessorOptions _options;

    private readonly ILogger _logger;

    /// <param name="verifier">verifies the SETs</param>
    /// <param name="dedupStore">remembers processed SETs, <see langword="null"/> to process duplicates</param>
    /// <param name="handlers">invoked in order for every SET</param>
    /// <param name="options">metrics, stream verification and critical subject members, the defaults if <see langword="null"/></param>
    /// <param name="logger">logs what happens to SETs, nothing if <see langword="null"/></param>
    public SsfSetProcessor(ISsfSetVerifier verifier, ISsfJtiDedupStore? dedupStore, IEnumerable<ISsfEventHandler> handlers,
            SsfSetProcessorOptions? options = null, ILogger<SsfSetProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(handlers);
        _verifier = verifier;
        _dedupStore = dedupStore;
        _handlers = handlers.ToList().AsReadOnly();
        _options = options ?? new SsfSetProcessorOptions();
        _logger = logger ?? NullLogger<SsfSetProcessor>.Instance;
    }

    /// <summary>
    /// How SETs are processed. Read for every SET.
    /// </summary>
    public SsfSetProcessorOptions Options => _options;

    /// <summary>
    /// The handlers, in the order they are invoked.
    /// </summary>
    public IReadOnlyList<ISsfEventHandler> Handlers => _handlers;

    /// <summary>
    /// Verifies and handles a SET that was pushed to the receiver.
    /// </summary>
    /// <seealso cref="ProcessAsync(string, SsfDeliveryMethod, CancellationToken)"/>
    public Task<SsfSetProcessingOutcome> ProcessAsync(string encodedSet, CancellationToken cancellationToken = default)
    {
        return ProcessAsync(encodedSet, SsfDeliveryMethod.Push, cancellationToken);
    }

    /// <summary>
    /// Verifies and handles the given SET.
    /// </summary>
    /// <param name="encodedSet">the SET in JWS compact serialization</param>
    /// <param name="deliveryMethod">how the SET was delivered</param>
    /// <param name="cancellationToken">cancels the processing; a cancelled SET is forgotten, so that a redelivery handles it</param>
    /// <exception cref="SsfSetVerificationException">if the SET is invalid</exception>
    /// <exception cref="SsfTransmitterUnavailableException">if the transmitter's metadata or keys cannot be obtained</exception>
    /// <exception cref="SsfEventHandlingException">if a handler failed, the SET may be delivered again</exception>
    public async Task<SsfSetProcessingOutcome> ProcessAsync(string encodedSet, SsfDeliveryMethod deliveryMethod,
            CancellationToken cancellationToken)
    {
        ISsfReceiverMetrics metrics = _options.Metrics;
        string? issuer = SsfSetClaims.UnverifiedIssuer(encodedSet);
        SsfEventToken eventToken;
        SsfEventContext eventContext;
        try
        {
            eventToken = await _verifier.VerifyAsync(encodedSet, cancellationToken).ConfigureAwait(false);
            eventContext = new SsfEventContext(eventToken);
            _options.StreamVerifications?.Invoke(eventToken.Iss)?.Validate(eventContext);
            CheckCriticalSubjectMembers(eventContext);
        }
        catch (SsfSetVerificationException)
        {
            metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.Invalid);
            throw;
        }
        catch (SsfTransmitterUnavailableException)
        {
            metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.Unavailable);
            throw;
        }
        issuer = eventToken.Iss;
        SsfJtiClaim? claim = null;
        if (_dedupStore is not null)
        {
            claim = await _dedupStore.ClaimAsync(eventToken, cancellationToken).ConfigureAwait(false);
            switch (claim.State)
            {
                case SsfJtiClaimState.Processed:
                    _logger.LogDebug("Skipping SET {Jti}, it was processed before", eventToken.Jti);
                    metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.Duplicate);
                    return SsfSetProcessingOutcome.Duplicate;
                case SsfJtiClaimState.InProgress:
                    _logger.LogDebug("Leaving SET {Jti} for a redelivery, another instance is handling it", eventToken.Jti);
                    metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.InProgress);
                    throw new SsfSetInProgressException($"SET {eventToken.Jti} is being handled by another instance");
                case SsfJtiClaimState.New:
                    break;
            }
        }
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received SET {Jti} ({Delivery}) with events {Events}", eventToken.Jti, deliveryMethod,
                string.Join(", ", eventContext.EventTypes.Select(SsfEventTypes.AliasOf)));
        }
        if (eventContext.HasEvent(SsfEventTypes.SsfStreamVerification))
        {
            _logger.LogInformation("Received stream verification event from {Issuer}", eventToken.Iss);
        }
        Exception? failure = null;
        foreach (ISsfEventHandler handler in _handlers)
        {
            try
            {
                await handler.HandleAsync(eventContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // the receiver is shutting down: let a redelivery handle the SET
                if (claim is not null)
                {
                    await _dedupStore!.ForgetAsync(eventToken, claim, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception ex)
            {
                // a failing handler must not keep the others from reacting to the event
                _logger.LogError(ex, "ISsfEventHandler {Handler} failed for SET {Jti}", handler.GetType().FullName, eventToken.Jti);
                failure ??= ex;
            }
        }
        if (failure is not null)
        {
            if (claim is not null)
            {
                await _dedupStore!.ForgetAsync(eventToken, claim, cancellationToken).ConfigureAwait(false);
            }
            metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.Failed);
            throw new SsfEventHandlingException($"Could not handle SET {eventToken.Jti}", failure);
        }
        if (claim is not null)
        {
            await _dedupStore!.ProcessedAsync(eventToken, claim, cancellationToken).ConfigureAwait(false);
        }
        metrics.SetReceived(issuer, deliveryMethod, SsfSetOutcome.Handled);
        foreach (string eventType in eventContext.EventTypes)
        {
            metrics.EventHandled(issuer, eventType, deliveryMethod);
        }
        return SsfSetProcessingOutcome.Handled;
    }

    private void CheckCriticalSubjectMembers(SsfEventContext eventContext)
    {
        IReadOnlyCollection<string>? critical = _options.CriticalSubjectMembers?.Invoke(eventContext.EventToken.Iss);
        if (critical is null || critical.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> present = eventContext.Subject.MemberNames();
        List<string> notUnderstood = critical
            .Where(member => present.Contains(member) && !_options.UnderstoodSubjectMembers.Contains(member))
            .ToList();
        if (notUnderstood.Count > 0)
        {
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidRequest,
                $"The subject has the members [{string.Join(", ", notUnderstood)}] which the transmitter declared critical"
                    + " and this receiver does not understand");
        }
    }
}
