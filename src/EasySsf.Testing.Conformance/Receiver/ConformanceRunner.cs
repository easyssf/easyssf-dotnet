using EasySsf.Core;

using Microsoft.Extensions.Logging;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// Starts and keeps the current <see cref="ConformanceRun"/>. There is at most one run at a time,
/// since the suite's tests run one after another and the push endpoint belongs to the current run.
/// </summary>
public sealed class ConformanceRunner
{
    private readonly ConformanceReceiverSettings _settings;

    private readonly HttpClient _httpClient;

    private readonly ILoggerFactory? _loggerFactory;

    private readonly Lock _lock = new();

    private int _runIds;

    private volatile ConformanceRun? _current;

    /// <param name="settings">the settings of the receiver under test</param>
    /// <param name="httpClient">calls the transmitter; trusts the certificate of the suite</param>
    /// <param name="loggerFactory">for the logs of the runs, none if <see langword="null"/></param>
    public ConformanceRunner(ConformanceReceiverSettings settings, HttpClient httpClient, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        _settings = settings;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// The settings the runs use.
    /// </summary>
    public ConformanceReceiverSettings Settings => _settings;

    /// <summary>
    /// The current run, <see langword="null"/> before the first.
    /// </summary>
    public ConformanceRun? Current => _current;

    /// <summary>
    /// Starts a run, stopping the current one if it is still active.
    /// </summary>
    /// <param name="scenario">what the receiver does with its stream</param>
    /// <param name="issuer">the issuer of the emulated transmitter, <see langword="null"/> for the configured one (<c>Transmitter:Issuer</c>)</param>
    /// <param name="deliveryMethod">the delivery method to request, <see langword="null"/> for the configured one (<c>Delivery:Method</c>)</param>
    /// <param name="idleTimeout">how long the run waits for further events before it deletes the stream, <see langword="null"/> for the configured one (<c>Run:IdleTimeout</c>)</param>
    public ConformanceRun Start(ConformanceScenario scenario, string? issuer = null, SsfDeliveryMethod? deliveryMethod = null, TimeSpan? idleTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            issuer = _settings.Transmitter.Issuer;
            if (string.IsNullOrWhiteSpace(issuer))
            {
                throw new ArgumentException("Transmitter:Issuer must be set when no issuer is given for the run", nameof(issuer));
            }
        }
        lock (_lock)
        {
            ConformanceRun? previous = _current;
            if (previous is { IsActive: true })
            {
                previous.Stop();
            }
            ConformanceRun run = new(Interlocked.Increment(ref _runIds).ToString(System.Globalization.CultureInfo.InvariantCulture), scenario, issuer,
                deliveryMethod ?? _settings.Delivery.Method, _settings, _httpClient, _loggerFactory?.CreateLogger<ConformanceRun>());
            if (idleTimeout is { } timeout)
            {
                run.IdleTimeout = timeout;
            }
            _current = run;
            run.Start();
            return run;
        }
    }
}
