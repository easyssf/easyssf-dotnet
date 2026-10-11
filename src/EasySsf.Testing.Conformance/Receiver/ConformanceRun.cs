using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Core.Metadata;
using EasySsf.Core.Stream;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// The state of a <see cref="ConformanceRun"/>.
/// </summary>
public enum ConformanceRunStatus
{
    Starting,

    Running,

    /// <summary>
    /// The scenario was played and the stream deleted.
    /// </summary>
    Finished,

    /// <summary>
    /// The transmitter created a stream of another issuer; the receiver deleted it and stopped.
    /// </summary>
    RefusedStream,

    Failed,

    Stopped,
}

/// <summary>
/// One line of the log of a run.
/// </summary>
public sealed record ConformanceRunLogEntry(DateTimeOffset Time, string Message);

/// <summary>
/// A SET the run received, accepted or rejected.
/// </summary>
public sealed record ConformanceReceivedSet(DateTimeOffset Time, string Jti, IReadOnlyList<string> Events, string Outcome);

/// <summary>
/// One test run: a receiver assembled from the easyssf receiver library that creates its stream at
/// the emulated transmitter, plays the operations of its <see cref="ConformanceScenario"/>, accepts
/// the events the transmitter delivers and deletes the stream once no more arrive.
/// <para>
/// Every run has its own receiver (keys, access token, de-duplication), since every test of the
/// suite emulates a new transmitter under the same issuer.
/// </para>
/// </summary>
public sealed class ConformanceRun
{
    private readonly ConformanceReceiverSettings _settings;

    private readonly ILogger _logger;

    private readonly SsfTransmitterMetadataResolver _metadataResolver;

    private readonly SsfStreamClient _streamClient;

    private readonly SsfStreamVerification _verification;

    private readonly RecordingStreamVerification _recordingVerification;

    private readonly JwtSsfSetVerifier _verifier;

    private readonly SsfSetProcessor _processor;

    private readonly SsfPushHandler _pushHandler;

    private readonly SsfPoller? _poller;

    private readonly string _pushAuthorizationHeader;

    private readonly ConcurrentQueue<ConformanceRunLogEntry> _log = new();

    private readonly ConcurrentQueue<ConformanceReceivedSet> _receivedSets = new();

    private readonly CancellationTokenSource _stopping = new();

    private int _verifiedCount;

    private int _rejectedVerifications;

    private volatile SsfStreamConfiguration? _stream;

    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    private volatile bool _streamDeleted;

    private volatile Task? _task;

    private volatile ConformanceRunStatus _status = ConformanceRunStatus.Starting;

    /// <param name="id">the id of the run, unique within the runner</param>
    /// <param name="scenario">what the receiver does with its stream</param>
    /// <param name="issuer">the issuer of the emulated transmitter, i.e. the test instance of the suite</param>
    /// <param name="deliveryMethod">how the transmitter is to deliver the events</param>
    /// <param name="settings">the settings of the receiver under test</param>
    /// <param name="httpClient">calls the transmitter; trusts the certificate of the suite</param>
    /// <param name="logger">logs what the run does, in addition to its own log</param>
    public ConformanceRun(string id, ConformanceScenario scenario, string issuer, SsfDeliveryMethod deliveryMethod,
            ConformanceReceiverSettings settings, HttpClient httpClient, ILogger<ConformanceRun>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        Id = id;
        Scenario = scenario;
        Issuer = issuer;
        DeliveryMethod = deliveryMethod;
        _settings = settings;
        _logger = logger ?? NullLogger<ConformanceRun>.Instance;
        IdleTimeout = settings.Run.IdleTimeout;
        _metadataResolver = new SsfTransmitterMetadataResolver(issuer, null, httpClient);
        ISsfTransmitterTokenProvider tokenProvider = TokenProvider(settings.Auth, httpClient, _metadataResolver);
        _streamClient = new SsfStreamClient(httpClient, tokenProvider, _metadataResolver);
        _verification = new SsfStreamVerification { StreamId = () => StreamId };
        _recordingVerification = new RecordingStreamVerification(this);
        _verifier = new JwtSsfSetVerifier(issuer, async cancellationToken =>
        {
            SsfTransmitterMetadata metadata = await _metadataResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            return metadata.JwksUri ?? throw new SsfTransmitterUnavailableException("The transmitter metadata of " + issuer + " names no jwks_uri");
        }, httpClient, new SsfSetVerifierOptions { ExpectedAudiencesProvider = () => _stream?.Audience ?? [] });
        _processor = new SsfSetProcessor(new RecordingVerifier(this), new InMemorySsfJtiDedupStore(10_000),
            [SsfEventHandler.Of(RecordHandledSetAsync)], new SsfSetProcessorOptions { StreamVerifications = _ => _recordingVerification });
        byte[] secret = RandomNumberGenerator.GetBytes(24);
        _pushAuthorizationHeader = "Bearer " + Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _pushHandler = new SsfPushHandler(_processor, _pushAuthorizationHeader);
        if (deliveryMethod == SsfDeliveryMethod.Poll)
        {
            _poller = new SsfPoller(httpClient, tokenProvider,
                () => (_stream is { } stream && !_streamDeleted) ? stream.DeliveryEndpointUrl : null, _processor,
                new SsfPollerOptions { Interval = settings.Delivery.PollInterval, InitialDelay = TimeSpan.Zero });
        }
    }

    public string Id { get; }

    public ConformanceScenario Scenario { get; }

    public string Issuer { get; }

    public SsfDeliveryMethod DeliveryMethod { get; }

    public ConformanceRunStatus Status => _status;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How long the run waits for further events before it deletes the stream; the configured
    /// <c>Run:IdleTimeout</c> unless set before <see cref="Start"/>.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; }

    /// <summary>
    /// The id of the stream the run created, once it has.
    /// </summary>
    public string? StreamId => _stream?.StreamId;

    public IReadOnlyList<ConformanceRunLogEntry> Log => _log.ToArray();

    public IReadOnlyList<ConformanceReceivedSet> ReceivedSets => _receivedSets.ToArray();

    /// <summary>
    /// Whether the run is still going on.
    /// </summary>
    public bool IsActive => _status is ConformanceRunStatus.Starting or ConformanceRunStatus.Running;

    /// <summary>
    /// Completes when the run has ended.
    /// </summary>
    public Task Completion => _task ?? Task.CompletedTask;

    /// <summary>
    /// Starts the run in the background.
    /// </summary>
    public void Start()
    {
        if (_task is not null)
        {
            throw new InvalidOperationException("The run was started already");
        }
        _task = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>
    /// Ends the run: the stream is deleted. Returns at once, see <see cref="Completion"/>.
    /// </summary>
    public void Stop()
    {
        _stopping.Cancel();
    }

    /// <summary>
    /// Ends the run and waits until it has.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a SET the transmitter pushed to this run.
    /// </summary>
    public Task<SsfPushResponse> PushAsync(string? authorizationHeader, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        Touch();
        return _pushHandler.HandleAsync(authorizationHeader, body, cancellationToken);
    }

    private static ISsfTransmitterTokenProvider TokenProvider(ConformanceReceiverSettings.AuthSettings auth, HttpClient httpClient,
            SsfTransmitterMetadataResolver metadataResolver)
    {
        if (auth.Mode == ConformanceAuthMode.Static)
        {
            return SsfTransmitterTokenProviders.Fixed(auth.AccessToken);
        }
        return new LazyClientCredentialsTokenProvider(auth, httpClient, metadataResolver);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        _status = ConformanceRunStatus.Running;
        ConformanceRunStatus final;
        try
        {
            await ExecuteAsync(stopping).ConfigureAwait(false);
            final = (_status == ConformanceRunStatus.Running) ? ConformanceRunStatus.Finished : _status;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Record("Run stopped");
            final = ConformanceRunStatus.Stopped;
        }
        catch (Exception ex)
        {
            Record("Run failed: " + ex.Message);
            _logger.LogError(ex, "Conformance run {Id} failed", Id);
            final = ConformanceRunStatus.Failed;
        }
        // the run stays active until the stream is gone, so that its status says the cleanup is done
        if (_poller is not null)
        {
            await _poller.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await DeleteStreamAsync().ConfigureAwait(false);
        _status = final;
    }

    private async Task ExecuteAsync(CancellationToken stopping)
    {
        Record($"Scenario {Scenario.Alias()} with {DeliveryMethod} delivery against {Issuer}");
        SsfTransmitterMetadata metadata = await _metadataResolver.ResolveAsync(stopping).ConfigureAwait(false);
        Record($"Transmitter metadata: jwks_uri {metadata.JwksUri}, configuration_endpoint {metadata.ConfigurationEndpoint}");

        string description = "easyssf conformance run " + Id;
        SsfStreamConfiguration desired = DesiredStream(description);
        try
        {
            _stream = await _streamClient.CreateStreamAsync(desired, stopping).ConfigureAwait(false);
        }
        catch (SsfStreamIssuerMismatchException ex)
        {
            Record("Refusing the stream: " + ex.Message);
            _stream = ex.Stream;
            await DeleteStreamAsync().ConfigureAwait(false);
            _status = ConformanceRunStatus.RefusedStream;
            return;
        }
        SsfStreamConfiguration stream = _stream;
        Record($"Created stream {StreamId}: audience [{string.Join(", ", stream.Audience)}], delivery {stream.DeliveryMethod} {stream.DeliveryEndpointUrl},"
            + $" events delivered [{string.Join(", ", Aliases(stream.EventsDelivered))}]");
        _poller?.Start();
        if (Scenario == ConformanceScenario.CreateDelete)
        {
            return;
        }

        SsfStreamConfiguration read = await _streamClient.GetStreamAsync(StreamId!, stopping).ConfigureAwait(false);
        Record($"Read stream configuration: events requested [{string.Join(", ", Aliases(read.EventsRequested))}]");
        SsfStreamStatus streamStatus = await _streamClient.GetStatusAsync(StreamId!, stopping).ConfigureAwait(false);
        Record("Read stream status: " + streamStatus.Status);

        if (Scenario == ConformanceScenario.StreamManagement)
        {
            SsfStreamConfiguration updated = await _streamClient.UpdateStreamAsync(StreamId!,
                new JsonObject { ["description"] = description + " (updated)" }, stopping).ConfigureAwait(false);
            Record($"Updated stream: description '{updated.Description}'");
            SsfStreamConfiguration replaced = await _streamClient.ReplaceStreamAsync(StreamId!, DesiredStream(description + " (replaced)"), stopping)
                .ConfigureAwait(false);
            Record($"Replaced stream: description '{replaced.Description}'");
        }

        await VerifyStreamAsync(stopping).ConfigureAwait(false);

        if (Scenario == ConformanceScenario.StatusUpdate)
        {
            SsfStreamStatus paused = await _streamClient.UpdateStatusAsync(StreamId!, SsfStreamStatus.Paused, "conformance run", stopping).ConfigureAwait(false);
            Record("Pausing the stream: " + paused.Status);
            await Task.Delay(TimeSpan.FromSeconds(3), stopping).ConfigureAwait(false);
            SsfStreamStatus enabled = await _streamClient.UpdateStatusAsync(StreamId!, SsfStreamStatus.Enabled, "conformance run", stopping).ConfigureAwait(false);
            Record("Enabling the stream: " + enabled.Status);
        }
        if (Scenario == ConformanceScenario.RemoveSubject)
        {
            JsonObject subject = [];
            foreach ((string member, string value) in _settings.Stream.EffectiveSubjectToRemove)
            {
                subject[member] = value;
            }
            await _streamClient.RemoveSubjectAsync(StreamId!, subject, stopping).ConfigureAwait(false);
            Record($"Removed subject {subject.ToJsonString()} from the stream");
        }

        TimeSpan idleTimeout = IdleTimeout;
        Record($"Accepting events until none arrived for {idleTimeout.TotalSeconds:0}s");
        DateTimeOffset phaseStart = DateTimeOffset.UtcNow;
        while (true)
        {
            await Task.Delay(500, stopping).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset lastActivity = new(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);
            DateTimeOffset latest = (lastActivity > phaseStart) ? lastActivity : phaseStart;
            if (now - latest > idleTimeout)
            {
                Record($"No event for {idleTimeout.TotalSeconds:0}s");
                break;
            }
            if (now - StartedAt > _settings.Run.MaxDuration)
            {
                Record("Maximum run duration reached");
                break;
            }
        }
    }

    private async Task VerifyStreamAsync(CancellationToken stopping)
    {
        TimeSpan timeout = _settings.Run.VerificationTimeout;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            int verifiedBefore = Volatile.Read(ref _verifiedCount);
            int rejectedBefore = Volatile.Read(ref _rejectedVerifications);
            string state = await _verification.RequestVerificationAsync(_streamClient, StreamId!, stopping).ConfigureAwait(false);
            Record("Requested stream verification with state " + state);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Volatile.Read(ref _verifiedCount) > verifiedBefore)
                {
                    return;
                }
                if (Volatile.Read(ref _rejectedVerifications) > rejectedBefore)
                {
                    Record("Rejected a verification event, requesting the verification again");
                    break;
                }
                await Task.Delay(200, stopping).ConfigureAwait(false);
            }
            if (Volatile.Read(ref _rejectedVerifications) == rejectedBefore)
            {
                Record($"No verification event within {timeout.TotalSeconds:0}s, going on without it");
                return;
            }
        }
    }

    private SsfStreamConfiguration DesiredStream(string description)
    {
        IReadOnlyList<string> events = _settings.Stream.EffectiveEventsRequested;
        return (DeliveryMethod == SsfDeliveryMethod.Poll)
            ? SsfStreamConfiguration.Poll(events, description)
            : SsfStreamConfiguration.Push(_settings.Delivery.PushUrl, _pushAuthorizationHeader, events, description);
    }

    private async Task DeleteStreamAsync()
    {
        string? streamId = StreamId;
        if (streamId is null || _streamDeleted)
        {
            return;
        }
        _streamDeleted = true;
        try
        {
            await _streamClient.DeleteStreamAsync(streamId, CancellationToken.None).ConfigureAwait(false);
            Record("Deleted stream " + streamId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record($"Could not delete stream {streamId}: {ex.Message}");
        }
    }

    private Task RecordHandledSetAsync(SsfEventContext eventContext, CancellationToken cancellationToken)
    {
        SsfEventToken token = eventContext.EventToken;
        if (eventContext.HasEvent(SsfEventTypes.SsfStreamVerification))
        {
            JsonElement? verification = eventContext.EventFor(SsfEventTypes.SsfStreamVerification);
            string? state = (verification is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("state", out JsonElement value)
                && value.ValueKind == JsonValueKind.String) ? value.GetString() : null;
            Record($"Verification event {token.Jti} accepted, " + ((state is not null) ? "state " + state : "no state (initiated by the transmitter)"));
            Interlocked.Increment(ref _verifiedCount);
        }
        RecordSet(token.Jti, Aliases(token.Events.Keys), "accepted");
        return Task.CompletedTask;
    }

    private void RecordSet(string jti, IReadOnlyList<string> events, string outcome)
    {
        DateTimeOffset now = Touch();
        _receivedSets.Enqueue(new ConformanceReceivedSet(now, jti, events, outcome));
        Record($"SET {jti} [{string.Join(", ", events)}]: {outcome}");
    }

    private DateTimeOffset Touch()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Volatile.Write(ref _lastActivityTicks, now.UtcTicks);
        return now;
    }

    private void Record(string message)
    {
        _log.Enqueue(new ConformanceRunLogEntry(DateTimeOffset.UtcNow, message));
        _logger.LogInformation("[run {Id}] {Message}", Id, message);
    }

    private static IReadOnlyList<string> Aliases(IEnumerable<string> eventTypes)
    {
        return eventTypes.Select(SsfEventTypes.AliasOf).ToList();
    }

    private static string JtiOf(string encodedSet)
    {
        try
        {
            string[] parts = encodedSet.Split('.');
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("jti", out JsonElement jti) ? jti.ToString() : "?";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            return "?";
        }
    }

    /// <summary>
    /// The token endpoint of the emulated authorization server lives next to the transmitter; it is
    /// looked up with the first call, so that a run can start while the suite is down.
    /// </summary>
    private sealed class LazyClientCredentialsTokenProvider(ConformanceReceiverSettings.AuthSettings auth, HttpClient httpClient,
            SsfTransmitterMetadataResolver metadataResolver) : ISsfTransmitterTokenProvider
    {
        private volatile ClientCredentialsSsfTransmitterTokenProvider? _delegate;

        public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            ClientCredentialsSsfTransmitterTokenProvider? provider = _delegate;
            if (provider is null)
            {
                SsfTransmitterMetadata metadata = await metadataResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
                SsfClientCredentialsOptions options = new()
                {
                    AuthenticateWithRequestBody = auth.ClientAuthenticationMethod == ConformanceClientAuthenticationMethod.Post,
                };
                foreach (string scope in auth.EffectiveScopes)
                {
                    options.Scopes.Add(scope);
                }
                provider = new ClientCredentialsSsfTransmitterTokenProvider(httpClient, new Uri(metadata.Issuer.TrimEnd('/') + "/token"), auth.ClientId,
                    auth.ClientSecret, options);
                _delegate = provider;
            }
            return await provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Invalidate()
        {
            _delegate?.Invalidate();
        }
    }

    /// <summary>
    /// Records the SETs the verifier rejects.
    /// </summary>
    private sealed class RecordingVerifier(ConformanceRun run) : ISsfSetVerifier
    {
        public async Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
        {
            try
            {
                return await run._verifier.VerifyAsync(encodedSet, cancellationToken).ConfigureAwait(false);
            }
            catch (SsfSetVerificationException ex)
            {
                run.RecordSet(JtiOf(encodedSet), [], $"rejected with {ex.ErrorCode}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Records the verification events it rejects.
    /// </summary>
    private sealed class RecordingStreamVerification(ConformanceRun run) : ISsfStreamVerification
    {
        public void Validate(SsfEventContext eventContext)
        {
            try
            {
                run._verification.Validate(eventContext);
            }
            catch (SsfSetVerificationException ex)
            {
                Interlocked.Increment(ref run._rejectedVerifications);
                run.RecordSet(eventContext.EventToken.Jti, ["SsfStreamVerification"], $"rejected with {ex.ErrorCode}: {ex.Message}");
                throw;
            }
        }
    }
}
