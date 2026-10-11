using System.Collections.Concurrent;
using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Set;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Validates stream verification events (SSF 1.0, section 8.1.4.1) and keeps track of the
/// verifications this receiver requested.
/// <para>
/// A verification event has to be about the stream of the receiver: its <c>sub_id</c> is an
/// <c>opaque</c> identifier with the identifier of the stream. If it echoes a <c>state</c>, that
/// has to be a state the receiver sent with a verification request that was not answered yet. An
/// event without <c>state</c> was initiated by the transmitter and is accepted.
/// </para>
/// </summary>
public sealed class SsfStreamVerification : ISsfStreamVerification
{
    private readonly ConcurrentDictionary<string, byte> _pendingStates = new(StringComparer.Ordinal);

    private readonly ILogger _logger;

    public SsfStreamVerification(ILogger<SsfStreamVerification>? logger = null)
    {
        _logger = logger ?? NullLogger<SsfStreamVerification>.Instance;
    }

    /// <summary>
    /// Supplies the identifier of the stream of this receiver, <see langword="null"/> as long as it
    /// is not known. The subject of verification events is not checked while it is unknown.
    /// </summary>
    public Func<string?> StreamId { get; set; } = () => null;

    /// <summary>
    /// Whether a verification event with a <c>state</c> is rejected when the receiver is not
    /// waiting for the answer to a verification request. By default such an event is accepted,
    /// since some transmitters add a state to the verification events they initiate themselves.
    /// </summary>
    public bool RejectUnrequestedState { get; set; }

    /// <summary>
    /// Creates a state for a verification request and remembers it until the verification event
    /// that echoes it arrived.
    /// </summary>
    public string NewState()
    {
        string state = Guid.NewGuid().ToString();
        _pendingStates[state] = 0;
        return state;
    }

    /// <summary>
    /// Asks the transmitter to send a verification event on the stream.
    /// </summary>
    /// <returns>the state the verification event has to echo</returns>
    public async Task<string> RequestVerificationAsync(SsfStreamClient streamClient, string streamId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamClient);
        string state = NewState();
        try
        {
            await streamClient.RequestVerificationAsync(streamId, state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingStates.TryRemove(state, out _);
            throw;
        }
        return state;
    }

    /// <summary>
    /// Whether a verification request was sent that has not been answered yet.
    /// </summary>
    public bool IsPending => !_pendingStates.IsEmpty;

    /// <summary>
    /// Validates the verification event of a SET, if it has one.
    /// </summary>
    /// <exception cref="SsfSetVerificationException">
    /// if the event is about another stream or echoes a state that is not expected
    /// </exception>
    public void Validate(SsfEventContext eventContext)
    {
        ArgumentNullException.ThrowIfNull(eventContext);
        if (!eventContext.HasEvent(SsfEventTypes.SsfStreamVerification))
        {
            return;
        }
        string? expectedStreamId = StreamId();
        JsonElement? subjectId = eventContext.EventToken.SubjectId;
        // SSF 1.0 8.1.4.1: the sub_id of a verification event is opaque and names the stream
        if (expectedStreamId is not null && (subjectId is null || subjectId.Value.StringMember("format") != "opaque"
                || subjectId.Value.StringMember("id") != expectedStreamId))
        {
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidRequest,
                "The verification event is not about the stream of this receiver");
        }
        string? state = eventContext.EventFor(SsfEventTypes.SsfStreamVerification)?.TextMember("state");
        if (state is null || _pendingStates.TryRemove(state, out _))
        {
            return;
        }
        if (!_pendingStates.IsEmpty || RejectUnrequestedState)
        {
            throw new SsfSetVerificationException(SsfSetVerificationException.InvalidState,
                "The state of the verification event is not the one this receiver sent");
        }
        _logger.LogDebug("Accepting a verification event with a state although no verification was requested");
    }
}
