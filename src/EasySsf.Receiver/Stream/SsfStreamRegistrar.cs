using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Core.Stream;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Stream;

/// <summary>
/// Makes the stream of this receiver known after <see cref="Start"/>, without holding the startup
/// of the application up: the transmitter is called in the background and retried until it answers.
/// <para>
/// Either looks up a stream that was created at the transmitter by its identifier, or manages the
/// stream itself: it reuses the stream the receiver already has at the transmitter, brings it in
/// line with the desired configuration, or creates it.
/// </para>
/// <para>
/// <see cref="AddListener">Listeners</see> learn when the stream is registered;
/// <c>SsfTransmitter</c> uses that to have the poller fetch right away instead of an interval later.
/// </para>
/// </summary>
public sealed class SsfStreamRegistrar : IDisposable
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly SsfStreamClient _streamClient;

    private readonly SsfReceiverStream _receiverStream;

    private readonly string? _streamId;

    private readonly SsfStreamConfiguration? _desiredStream;

    private readonly TimeProvider _timeProvider;

    private readonly ILogger _logger;

    private readonly List<Action<SsfStreamConfiguration>> _listeners = [];

    private readonly Lock _lifecycle = new();

    private TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _stopping;

    private Task? _registration;

    private volatile SsfStreamRegistrarState _state = SsfStreamRegistrarState.NotStarted;

    private volatile string? _lastError;

    private SsfStreamRegistrar(SsfStreamClient streamClient, SsfReceiverStream receiverStream, string? streamId,
            SsfStreamConfiguration? desiredStream, TimeProvider? timeProvider, ILogger<SsfStreamRegistrar>? logger)
    {
        ArgumentNullException.ThrowIfNull(streamClient);
        ArgumentNullException.ThrowIfNull(receiverStream);
        _streamClient = streamClient;
        _receiverStream = receiverStream;
        _streamId = streamId;
        _desiredStream = desiredStream;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SsfStreamRegistrar>.Instance;
    }

    /// <summary>
    /// Looks up the stream with the given identifier.
    /// </summary>
    public static SsfStreamRegistrar ForExistingStream(SsfStreamClient streamClient, SsfReceiverStream receiverStream, string streamId,
            TimeProvider? timeProvider = null, ILogger<SsfStreamRegistrar>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        return new SsfStreamRegistrar(streamClient, receiverStream, streamId, null, timeProvider, logger);
    }

    /// <summary>
    /// Discovers, updates or creates the stream of the receiver.
    /// </summary>
    /// <param name="streamClient">calls the transmitter</param>
    /// <param name="receiverStream">told about the stream once it is known</param>
    /// <param name="desiredStream">the stream the receiver wants to have</param>
    /// <param name="timeProvider">paces the retries, the system clock if <see langword="null"/></param>
    /// <param name="logger">logs the registration, nothing if <see langword="null"/></param>
    /// <seealso cref="SsfStreamConfiguration.Push"/>
    /// <seealso cref="SsfStreamConfiguration.Poll"/>
    public static SsfStreamRegistrar ForManagedStream(SsfStreamClient streamClient, SsfReceiverStream receiverStream,
            SsfStreamConfiguration desiredStream, TimeProvider? timeProvider = null, ILogger<SsfStreamRegistrar>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(desiredStream);
        return new SsfStreamRegistrar(streamClient, receiverStream, null, desiredStream, timeProvider, logger);
    }

    /// <summary>
    /// Whether a stream this receiver manages is deleted at the transmitter when the application
    /// stops.
    /// </summary>
    public bool DeleteOnShutdown { get; set; }

    /// <summary>
    /// How long the first retry waits after a failed attempt, one second by default; the delay
    /// doubles up to 30 seconds.
    /// </summary>
    public TimeSpan InitialRetryDelay
    {
        get => _initialRetryDelay;
        set => _initialRetryDelay = (value > TimeSpan.Zero) ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "InitialRetryDelay must be positive");
    }

    /// <summary>
    /// Where the registration stands.
    /// </summary>
    public SsfStreamRegistrarState State => _state;

    /// <summary>
    /// Why the last attempt to look up or register the stream failed, <see langword="null"/> if it
    /// succeeded or none was made yet.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Whether <see cref="Start"/> was called and <see cref="StopAsync"/> was not.
    /// </summary>
    public bool IsRunning => _stopping is not null;

    /// <summary>
    /// Adds a listener that is told whenever <see cref="RegisterAsync"/> succeeded, on the thread
    /// that registered the stream. A listener that throws is logged and does not keep the stream
    /// from being used.
    /// </summary>
    public void AddListener(Action<SsfStreamConfiguration> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_listeners)
        {
            _listeners.Add(listener);
        }
    }

    /// <summary>
    /// Starts looking up or registering the stream in the background.
    /// </summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_stopping is not null)
            {
                return;
            }
            _state = SsfStreamRegistrarState.Registering;
            CancellationTokenSource stopping = new();
            _stopping = stopping;
            _registration = Task.Run(() => RegisterWithRetriesAsync(stopping.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops the background registration and deletes the stream if asked to.
    /// </summary>
    /// <seealso cref="DeleteOnShutdown"/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? stopping;
        Task? registration;
        lock (_lifecycle)
        {
            stopping = _stopping;
            registration = _registration;
            _stopping = null;
            _registration = null;
        }
        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            stopping.Dispose();
        }
        if (registration is not null)
        {
            try
            {
                await registration.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // the registration was cancelled
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("The SSF stream registration did not end within 5 seconds");
            }
        }
        string? registeredStreamId = _receiverStream.StreamId;
        if (DeleteOnShutdown && _desiredStream is not null && registeredStreamId is not null)
        {
            try
            {
                await _streamClient.DeleteStreamAsync(registeredStreamId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Deleted SSF stream {StreamId}", registeredStreamId);
            }
            catch (SsfStreamException ex)
            {
                _logger.LogWarning("Could not delete SSF stream {StreamId}: {Reason}", registeredStreamId, ex.Message);
            }
        }
    }

    private async Task RegisterWithRetriesAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = _initialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RegisterAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SsfStreamIssuerMismatchException ex)
            {
                // trying again would only create another stream that must not be used
                _logger.LogError("Not using the SSF stream: {Reason}", ex.Message);
                _lastError = ex.Message;
                _state = SsfStreamRegistrarState.Failed;
                await DeleteQuietlyAsync(ex.Stream.StreamId, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                _logger.LogWarning("Could not set up the SSF stream, trying again in {Delay}s: {Reason}", (int)delay.TotalSeconds, ex.Message);
                _logger.LogDebug(ex, "Cause of the failed SSF stream setup");
            }
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = (delay * 2 < MaxRetryDelay) ? delay * 2 : MaxRetryDelay;
        }
    }

    /// <summary>
    /// Looks up or registers the stream once.
    /// </summary>
    /// <returns>the stream</returns>
    /// <exception cref="SsfStreamException">if the transmitter could not be called</exception>
    public async Task<SsfStreamConfiguration> RegisterAsync(CancellationToken cancellationToken = default)
    {
        SsfStreamConfiguration stream;
        string action;
        if (_desiredStream is null)
        {
            stream = await _streamClient.GetStreamAsync(_streamId!, cancellationToken).ConfigureAwait(false);
            action = "Using";
        }
        else
        {
            IReadOnlyList<SsfStreamConfiguration> streams = await _streamClient.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
            SsfStreamConfiguration? existing = streams.FirstOrDefault(HasDesiredDelivery);
            if (existing is not null && HasDesiredEvents(existing))
            {
                stream = existing;
                action = "Using existing";
            }
            else if (existing is not null)
            {
                stream = await _streamClient.UpdateStreamAsync(existing.StreamId!, Changes(), cancellationToken).ConfigureAwait(false);
                action = "Updated";
            }
            else
            {
                try
                {
                    stream = await _streamClient.CreateStreamAsync(_desiredStream, cancellationToken).ConfigureAwait(false);
                    action = "Created";
                }
                catch (SsfStreamException ex) when (ex is not SsfStreamIssuerMismatchException && ex.StatusCode == 409 && streams.Count == 1)
                {
                    // the transmitter allows a single stream per receiver and it is configured differently
                    stream = await _streamClient.UpdateStreamAsync(streams[0].StreamId!, Changes(), cancellationToken).ConfigureAwait(false);
                    action = "Updated";
                }
            }
        }
        _receiverStream.Configuration = stream;
        _logger.LogInformation("{Action} SSF stream {StreamId} (delivery {Method} {Endpoint}, events {Events}, audience {Audience})",
            action, stream.StreamId, stream.DeliveryMethod, stream.DeliveryEndpointUrl?.ToString() ?? "",
            string.Join(", ", stream.EventsDelivered.Select(SsfEventTypes.AliasOf)), string.Join(", ", stream.Audience));
        _lastError = null;
        _state = SsfStreamRegistrarState.Registered;
        Action<SsfStreamConfiguration>[] listeners;
        lock (_listeners)
        {
            listeners = [.. _listeners];
        }
        foreach (Action<SsfStreamConfiguration> listener in listeners)
        {
            try
            {
                listener(stream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("A listener failed on the registration of SSF stream {StreamId}: {Reason}", stream.StreamId, ex.Message);
                _logger.LogDebug(ex, "Cause of the failed listener");
            }
        }
        return stream;
    }

    private async Task DeleteQuietlyAsync(string? streamId, CancellationToken cancellationToken)
    {
        if (streamId is null)
        {
            return;
        }
        try
        {
            await _streamClient.DeleteStreamAsync(streamId, cancellationToken).ConfigureAwait(false);
        }
        catch (SsfStreamException ex)
        {
            _logger.LogWarning("Could not delete SSF stream {StreamId}: {Reason}", streamId, ex.Message);
        }
    }

    private bool HasDesiredEvents(SsfStreamConfiguration stream)
    {
        return EventTypes(stream.EventsRequested).SetEquals(EventTypes(_desiredStream!.EventsRequested));
    }

    private bool HasDesiredDelivery(SsfStreamConfiguration stream)
    {
        if (stream.DeliveryMethod != _desiredStream!.DeliveryMethod)
        {
            return false;
        }
        return stream.DeliveryMethod == SsfDeliveryMethod.Poll.Uri() || stream.DeliveryEndpointUrl == _desiredStream.DeliveryEndpointUrl;
    }

    private JsonObject Changes()
    {
        JsonObject changes = new();
        if (_desiredStream!.Delivery is { } delivery)
        {
            changes["delivery"] = JsonObject.Create(delivery);
        }
        JsonArray events = [];
        foreach (string eventType in _desiredStream.EventsRequested)
        {
            events.Add((JsonNode)eventType);
        }
        changes["events_requested"] = events;
        if (_desiredStream.Description is not null)
        {
            changes["description"] = _desiredStream.Description;
        }
        return changes;
    }

    private static HashSet<string> EventTypes(IEnumerable<string> eventTypes)
    {
        return new HashSet<string>(eventTypes.Select(SsfEventTypes.Resolve), StringComparer.Ordinal);
    }

    public void Dispose()
    {
        _stopping?.Dispose();
    }
}
