using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Push;

/// <summary>
/// Handles SETs delivered using HTTP push (RFC 8935), independent of the web framework: call
/// <see cref="HandleAsync"/> from the endpoint the transmitter posts SETs to and send the returned
/// response.
/// <para>
/// A SET is verified and handled before it is acknowledged with <c>202 Accepted</c>. Invalid SETs
/// are rejected with <c>400 Bad Request</c> and an error document. When the SET could not be
/// processed for a reason the transmitter is not responsible for (its keys are temporarily not
/// retrievable, an event handler failed) a <c>5xx</c> status asks it to deliver the SET again.
/// </para>
/// </summary>
public sealed class SsfPushHandler
{
    /// <summary>
    /// The maximum size of a SET in bytes. An endpoint does not need to read more than one byte
    /// beyond it.
    /// </summary>
    public const int MaxSetSize = 256 * 1024;

    private const string AuthenticationFailed = "authentication_failed";

    private readonly SsfSetProcessor _processor;

    private readonly Func<string?, string?> _expectedAuthorizationHeaders;

    private readonly bool _headerDependsOnIssuer;

    private readonly ISsfReceiverMetrics _metrics;

    private readonly ILogger _logger;

    /// <param name="processor">processes the received SETs</param>
    /// <param name="expectedAuthorizationHeader">
    /// the exact <c>Authorization</c> header the transmitter must send, <see langword="null"/> to
    /// not check the header
    /// </param>
    /// <param name="metrics">records rejected transmitters, nothing if <see langword="null"/></param>
    /// <param name="logger">logs rejected SETs, nothing if <see langword="null"/></param>
    public SsfPushHandler(SsfSetProcessor processor, string? expectedAuthorizationHeader,
            ISsfReceiverMetrics? metrics = null, ILogger<SsfPushHandler>? logger = null)
        : this(processor, _ => expectedAuthorizationHeader, false, metrics, logger)
    {
    }

    /// <summary>
    /// For a receiver with several transmitters: the <c>Authorization</c> header a transmitter has
    /// to send is looked up by the issuer of the SET, which is read before the SET is verified.
    /// </summary>
    /// <param name="processor">processes the SETs</param>
    /// <param name="expectedAuthorizationHeaders">
    /// the exact <c>Authorization</c> header the transmitter with an issuer has to send,
    /// <see langword="null"/> if none is expected from it; called with <see langword="null"/>
    /// for a SET whose issuer cannot be read
    /// </param>
    /// <param name="metrics">records rejected transmitters, nothing if <see langword="null"/></param>
    /// <param name="logger">logs rejected SETs, nothing if <see langword="null"/></param>
    public SsfPushHandler(SsfSetProcessor processor, Func<string?, string?> expectedAuthorizationHeaders,
            ISsfReceiverMetrics? metrics = null, ILogger<SsfPushHandler>? logger = null)
        : this(processor, expectedAuthorizationHeaders, true, metrics, logger)
    {
    }

    private SsfPushHandler(SsfSetProcessor processor, Func<string?, string?> expectedAuthorizationHeaders,
            bool headerDependsOnIssuer, ISsfReceiverMetrics? metrics, ILogger<SsfPushHandler>? logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(expectedAuthorizationHeaders);
        _processor = processor;
        _expectedAuthorizationHeaders = expectedAuthorizationHeaders;
        _headerDependsOnIssuer = headerDependsOnIssuer;
        _metrics = metrics ?? NullSsfReceiverMetrics.Instance;
        _logger = logger ?? NullLogger<SsfPushHandler>.Instance;
    }

    /// <summary>
    /// Handles a push request.
    /// </summary>
    /// <param name="authorizationHeader">the <c>Authorization</c> header of the request, may be <see langword="null"/></param>
    /// <param name="body">the body of the request, the SET in JWS compact serialization</param>
    /// <param name="cancellationToken">cancels the processing</param>
    /// <returns>the response to send to the transmitter</returns>
    public async Task<SsfPushResponse> HandleAsync(string? authorizationHeader, ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
    {
        if (!_headerDependsOnIssuer && !IsAuthenticated(authorizationHeader, null))
        {
            _metrics.SetReceived(null, SsfDeliveryMethod.Push, SsfSetOutcome.Unauthenticated);
            return Error(401, AuthenticationFailed, "The transmitter could not be authenticated");
        }
        if (body.Length > MaxSetSize)
        {
            return Error(413, SsfSetVerificationException.InvalidRequest, "The SET is too large");
        }
        string encodedSet = Encoding.UTF8.GetString(body.Span).Trim();
        if (_headerDependsOnIssuer)
        {
            string? issuer = SsfSetClaims.UnverifiedIssuer(encodedSet);
            if (!IsAuthenticated(authorizationHeader, issuer))
            {
                _metrics.SetReceived(issuer, SsfDeliveryMethod.Push, SsfSetOutcome.Unauthenticated);
                return Error(401, AuthenticationFailed, "The transmitter could not be authenticated");
            }
        }
        try
        {
            await _processor.ProcessAsync(encodedSet, SsfDeliveryMethod.Push, cancellationToken).ConfigureAwait(false);
        }
        catch (SsfSetVerificationException ex)
        {
            _logger.LogDebug(ex, "Rejecting SET: {Reason}", ex.Message);
            return Error(400, ex.ErrorCode, ex.Message);
        }
        catch (SsfTransmitterUnavailableException ex)
        {
            _logger.LogWarning("Could not verify SET, asking the transmitter to deliver it again: {Reason}", ex.Message);
            _logger.LogDebug(ex, "Cause of the failed SET verification");
            return new SsfPushResponse(503, null);
        }
        catch (SsfEventHandlingException)
        {
            // already logged per handler
            return new SsfPushResponse(500, null);
        }
        return new SsfPushResponse(202, null);
    }

    private bool IsAuthenticated(string? authorizationHeader, string? issuer)
    {
        string? expected = _expectedAuthorizationHeaders(issuer);
        if (expected is null)
        {
            return true;
        }
        return authorizationHeader is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(authorizationHeader));
    }

    private static SsfPushResponse Error(int status, string errorCode, string? description)
    {
        JsonObject error = new() { ["err"] = errorCode };
        if (description is not null)
        {
            error["description"] = description;
        }
        return new SsfPushResponse(status, error.ToJsonString());
    }
}
