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

    /// <summary>Start() and StopAsync() are idempotent and safe to call from any thread</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    /// <summary>
    /// Guards <see cref="_current"/> and the publication of a registration: StopAsync() takes the
    /// run away under it, the registration checks that it is still the current run and publishes
    /// under it, so a registration either publishes before StopAsync() proceeds or not at all. A
    /// lock of its own, as StopAsync() must not hold it while it waits for the registration.
    /// </summary>
    private readonly Lock _publication = new();

    /// <summary>
    /// Streams created by registrations that outlived their StopAsync() while another registration
    /// had already started, and so could not publish them. The registration that follows adopts
    /// such a stream if it finds it, and deletes it once it publishes another; StopAsync() deletes
    /// what is left. Guarded by <see cref="_publication"/>.
    /// </summary>
    private readonly List<string> _unadoptedStreams = [];

    /// <summary>
    /// Streams whose deletion is under way, marked under <see cref="_publication"/> before the lock
    /// is released for the call: a registration does not adopt one of them, a slow delete would
    /// otherwise remove the stream a registration that followed uses.
    /// </summary>
    private readonly HashSet<string> _deletingStreams = new(StringComparer.Ordinal);

    private TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(1);

    private TimeSpan _stopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The current registration; a registration that finds another run here, or none, was stopped
    /// and publishes nothing. Written under <see cref="_publication"/>.
    /// </summary>
    private volatile Run? _current;

    /// <summary>
    /// The run that published the stream the receiver has, <see langword="null"/> once StopAsync()
    /// ran: tells a registration that outlived its StopAsync() whether the one that followed has
    /// published yet. Guarded by <see cref="_publication"/>.
    /// </summary>
    private Run? _publishedBy;

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
    /// How long <see cref="StopAsync"/> waits for a registration in flight, 5 seconds by default. A
    /// registration that takes longer, because its <c>HttpMessageHandler</c> does not observe the
    /// cancellation, is left to finish on its own and publishes nothing.
    /// </summary>
    public TimeSpan StopTimeout
    {
        get => _stopTimeout;
        set => _stopTimeout = (value > TimeSpan.Zero) ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "StopTimeout must be positive");
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
    public bool IsRunning => _current is not null;

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
    /// Starts looking up or registering the stream in the background. Calling it again while a
    /// registration runs has no effect.
    /// </summary>
    public void Start()
    {
        _lifecycle.Wait();
        try
        {
            if (_current is not null)
            {
                return;
            }
            _state = SsfStreamRegistrarState.Registering;
            Run run = new();
            lock (_publication)
            {
                _current = run;
            }
            // the token is read here, not on the pool thread: the run's source outlives it
            CancellationToken token = run.Stopping.Token;
            run.Task = Task.Run(() => RegisterWithRetriesAsync(run, token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the background registration and deletes the stream if asked to. Only a stream
    /// published since the last stop is deleted: the receiver keeps the stream after the stop, and
    /// it is not deleted twice.
    /// </summary>
    /// <seealso cref="DeleteOnShutdown"/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Run? run;
            bool published;
            List<string> unadopted;
            // a publication in progress completes first, so that the stream it publishes is the one
            // deleted below; none starts afterwards
            lock (_publication)
            {
                run = _current;
                _current = null;
                published = _publishedBy is not null;
                _publishedBy = null;
                unadopted = [.. _unadoptedStreams];
                _unadoptedStreams.Clear();
                _deletingStreams.UnionWith(unadopted);
            }
            if (run is not null)
            {
                await run.Stopping.CancelAsync().ConfigureAwait(false);
                try
                {
                    // a registration in flight ends with the cancelled call; one that completes
                    // regardless sees it is no longer current and publishes nothing
                    await run.Task!.WaitAsync(_stopTimeout, cancellationToken).ConfigureAwait(false);
                    run.Stopping.Dispose();
                }
                catch (TimeoutException)
                {
                    // the source stays undisposed, the registration still holds its token
                    _logger.LogWarning("The registration of the SSF stream is still in progress");
                }
            }
            string? registeredStreamId = _receiverStream.StreamId;
            if (DeleteOnShutdown && _desiredStream is not null && published && registeredStreamId is not null)
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
            await DeleteOrphansAsync(unadopted).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RegisterWithRetriesAsync(Run run, CancellationToken cancellationToken)
    {
        TimeSpan delay = _initialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Registration registration = await LookUpAsync(cancellationToken).ConfigureAwait(false);
                // decided under the lock, deleted after it: a slow transmitter must not hold up
                // StopAsync()
                List<string> orphans = [];
                lock (_publication)
                {
                    if (_current == run)
                    {
                        orphans = Publish(registration, run);
                    }
                    else if (_current is null)
                    {
                        // stopped while the transmitter was called: the result is not published,
                        // and a stream created on the way is not left behind
                        if (registration.Created && DeleteOnShutdown)
                        {
                            orphans.Add(registration.Stream.StreamId!);
                        }
                    }
                    else if (registration.Created && DeleteOnShutdown)
                    {
                        // stopped and started again while the transmitter was called, because
                        // StopAsync() gave up waiting: the registration that followed adopts the
                        // stream created on the way if it finds it, so it is not deleted on the
                        // spot. Once the other has published, the stream it uses is known: any
                        // other is an orphan.
                        string streamId = registration.Stream.StreamId!;
                        if (_publishedBy != _current)
                        {
                            // the other has not published yet, it decides on publishing
                            _unadoptedStreams.Add(streamId);
                            _logger.LogDebug("A registration that outlived its stop leaves SSF stream {StreamId} to the one that followed",
                                streamId);
                        }
                        else if (streamId != _receiverStream.StreamId)
                        {
                            orphans.Add(streamId);
                        }
                    }
                    _deletingStreams.UnionWith(orphans);
                }
                await DeleteOrphansAsync(orphans).ConfigureAwait(false);
                return;
            }
            catch (SsfStreamIssuerMismatchException ex)
            {
                // trying again would only find or create the same stream
                lock (_publication)
                {
                    if (_current == run)
                    {
                        _logger.LogError("Not using the SSF stream: {Reason}", ex.Message);
                        _lastError = ex.Message;
                        _state = SsfStreamRegistrarState.Failed;
                    }
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // the check and the report go together under the lock, so that a registration
                // stopped meanwhile, possibly replaced, reports no error of its own over the one
                // that followed
                lock (_publication)
                {
                    if (_current != run)
                    {
                        return;
                    }
                    _lastError = ex.Message;
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
    /// Looks up or registers the stream once, on the calling thread, and publishes it.
    /// </summary>
    /// <returns>the stream</returns>
    /// <exception cref="SsfStreamException">if the transmitter could not be called</exception>
    /// <exception cref="SsfStreamIssuerMismatchException">
    /// if the stream has another issuer than the transmitter; a stream created on the way is
    /// deleted again, one that existed is left alone
    /// </exception>
    public async Task<SsfStreamConfiguration> RegisterAsync(CancellationToken cancellationToken = default)
    {
        Registration registration = await LookUpAsync(cancellationToken).ConfigureAwait(false);
        List<string> orphans;
        lock (_publication)
        {
            orphans = Publish(registration, Run.Direct);
        }
        await DeleteOrphansAsync(orphans).ConfigureAwait(false);
        return registration.Stream;
    }

    private async Task<Registration> LookUpAsync(CancellationToken cancellationToken)
    {
        if (_desiredStream is null)
        {
            return new Registration(await _streamClient.GetStreamAsync(_streamId!, cancellationToken).ConfigureAwait(false), "Using", false);
        }
        IReadOnlyList<SsfStreamConfiguration> streams = await _streamClient.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> deleting = DeletingStreams();
        SsfStreamConfiguration? existing = streams
            .Where(stream => stream.StreamId is null || !deleting.Contains(stream.StreamId))
            .FirstOrDefault(HasDesiredDelivery);
        if (existing is not null && HasDesiredEvents(existing))
        {
            return new Registration(existing, "Using existing", false);
        }
        if (existing is not null)
        {
            return new Registration(await _streamClient.UpdateStreamAsync(existing.StreamId!, Changes(), cancellationToken).ConfigureAwait(false),
                "Updated", false);
        }
        try
        {
            return new Registration(await _streamClient.CreateStreamAsync(_desiredStream, cancellationToken).ConfigureAwait(false), "Created", true);
        }
        catch (SsfStreamIssuerMismatchException ex)
        {
            // the stream was created but must not be used, and trying again would only create
            // another one
            await DeleteQuietlyAsync(ex.Stream.StreamId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (SsfStreamException ex) when (ex.StatusCode == 409 && streams.Count == 1 && !deleting.Contains(streams[0].StreamId ?? ""))
        {
            // the transmitter allows a single stream per receiver and it is configured differently
            return new Registration(await _streamClient.UpdateStreamAsync(streams[0].StreamId!, Changes(), cancellationToken).ConfigureAwait(false),
                "Updated", false);
        }
    }

    /// <summary>
    /// Publishes the stream and tells the listeners; called under <see cref="_publication"/>.
    /// </summary>
    /// <returns>
    /// the streams of stopped registrations the caller deletes once it no longer holds the lock:
    /// those not adopted, as the receiver uses this one
    /// </returns>
    private List<string> Publish(Registration registration, Run run)
    {
        SsfStreamConfiguration stream = registration.Stream;
        if (stream.StreamId is not null && _deletingStreams.Contains(stream.StreamId))
        {
            // found by the look-up before a delete marked it: looked up again
            throw new SsfStreamException($"SSF stream {stream.StreamId} is being deleted", 0, null);
        }
        _receiverStream.Configuration = stream;
        _publishedBy = run;
        List<string> orphans = [.. _unadoptedStreams.Where(unadopted => unadopted != stream.StreamId)];
        _unadoptedStreams.Clear();
        _deletingStreams.UnionWith(orphans);
        _logger.LogInformation("{Action} SSF stream {StreamId} (delivery {Method} {Endpoint}, events {Events}, audience {Audience})",
            registration.Action, stream.StreamId, stream.DeliveryMethod, stream.DeliveryEndpointUrl?.ToString() ?? "",
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
        return orphans;
    }

    /// <summary>
    /// Deletes streams that registrations created and the receiver does not use; called without
    /// the <see cref="_publication"/> lock, as the transmitter may be slow, and not cancelled by the
    /// stop of the registration that found them, as they would be left behind.
    /// </summary>
    private async Task DeleteOrphansAsync(List<string> orphans)
    {
        foreach (string streamId in orphans)
        {
            _logger.LogInformation("Deleting SSF stream {StreamId}, created by a registration that was stopped before it could publish it",
                streamId);
            try
            {
                await DeleteQuietlyAsync(streamId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_publication)
                {
                    _deletingStreams.Remove(streamId);
                }
            }
        }
    }

    /// <returns>the streams a delete is under way for, to leave them out of a look-up</returns>
    private HashSet<string> DeletingStreams()
    {
        lock (_publication)
        {
            return new HashSet<string>(_deletingStreams, StringComparer.Ordinal);
        }
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
        Run? run = _current;
        run?.Stopping.Dispose();
        _lifecycle.Dispose();
    }

    /// <summary>
    /// One registration started by <see cref="Start"/>; its identity tells whether it is still the
    /// current one.
    /// </summary>
    private sealed class Run
    {
        /// <summary>stands for a direct call of <see cref="RegisterAsync"/></summary>
        public static readonly Run Direct = new();

        public CancellationTokenSource Stopping { get; } = new();

        public Task? Task { get; set; }
    }

    /// <summary>
    /// The outcome of a look-up: the stream, and whether it was created on the way.
    /// </summary>
    private sealed record Registration(SsfStreamConfiguration Stream, string Action, bool Created);
}
