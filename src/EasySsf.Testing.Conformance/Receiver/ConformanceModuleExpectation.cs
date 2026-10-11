namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// What the receiver does for a test module of the suite's receiver plans and how its run is
/// expected to end.
/// </summary>
/// <param name="Scenario">the scenario the receiver plays</param>
/// <param name="RunStatus">the status the run has to end with</param>
/// <param name="IdleTimeout">how long the run waits for further events before it deletes the stream</param>
public sealed record ConformanceModuleExpectation(ConformanceScenario Scenario, ConformanceRunStatus RunStatus, TimeSpan IdleTimeout)
{
    /// <summary>
    /// How long the receiver waits for further events before it deletes the stream. The suite
    /// delivers the events of most modules back to back (gaps of at most about 2 seconds), so this
    /// is far below the configured <c>Run:IdleTimeout</c>.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The idle timeout for the modules that pause between events on purpose: 20 seconds token
    /// lifetime in the access token expiry test, 15 seconds in the transmitter-initiated status
    /// change test.
    /// </summary>
    public static readonly TimeSpan PausingIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, (ConformanceRunStatus Status, TimeSpan IdleTimeout)> Outcomes = new(StringComparer.Ordinal)
    {
        // the transmitter's issuer differs from the one the stream is created at
        ["openid-ssf-receiver-stream-issuer-mismatch"] = (ConformanceRunStatus.RefusedStream, DefaultIdleTimeout),
        ["openid-ssf-receiver-access-token-expiry"] = (ConformanceRunStatus.Finished, PausingIdleTimeout),
        ["openid-ssf-receiver-transmitter-initiated-status-change"] = (ConformanceRunStatus.Finished, PausingIdleTimeout),
    };

    /// <summary>
    /// The expectation for a module, by its name.
    /// </summary>
    public static ConformanceModuleExpectation ForModule(string moduleName)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        (ConformanceRunStatus status, TimeSpan idleTimeout) = Outcomes.TryGetValue(moduleName, out var outcome)
            ? outcome : (ConformanceRunStatus.Finished, DefaultIdleTimeout);
        return new ConformanceModuleExpectation(ConformanceScenarios.ForModule(moduleName), status, idleTimeout);
    }
}
