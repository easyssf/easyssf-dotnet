namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// What the receiver does with its stream in a test run. All scenarios create the stream, check
/// its issuer, and delete it at the end; the ones that keep the stream read its configuration and
/// status, verify it and accept the events the transmitter delivers until none arrives for a while.
/// </summary>
public enum ConformanceScenario
{
    /// <summary>
    /// Fits the CAEP interop plan and most tests of the default plan.
    /// </summary>
    CaepInterop,

    /// <summary>
    /// Creates and deletes the stream, nothing else.
    /// </summary>
    CreateDelete,

    /// <summary>
    /// Additionally updates and replaces the stream configuration (receiver happy path test of the
    /// default plan).
    /// </summary>
    StreamManagement,

    /// <summary>
    /// Additionally pauses and enables the stream (stream status test of the default plan).
    /// </summary>
    StatusUpdate,

    /// <summary>
    /// Additionally removes a subject from the stream (removed subject test of the default plan).
    /// </summary>
    RemoveSubject,
}

/// <summary>
/// The aliases of the <see cref="ConformanceScenario"/>s and the scenario each test module needs.
/// </summary>
public static class ConformanceScenarios
{
    /// <summary>
    /// The test modules of the suite's receiver plans that need another scenario than
    /// <see cref="ConformanceScenario.CaepInterop"/>, which fits all tests that just expect a stream
    /// to be created, verified, fed and deleted.
    /// </summary>
    private static readonly Dictionary<string, ConformanceScenario> ModuleScenarios = new(StringComparer.Ordinal)
    {
        ["openid-ssf-receiver-happypath"] = ConformanceScenario.StreamManagement,
        ["openid-ssf-receiver-stream-create-delete"] = ConformanceScenario.CreateDelete,
        ["openid-ssf-receiver-stream-status-update"] = ConformanceScenario.StatusUpdate,
        ["openid-ssf-receiver-removed-subject-event"] = ConformanceScenario.RemoveSubject,
    };

    public static IReadOnlyList<ConformanceScenario> All { get; } = Enum.GetValues<ConformanceScenario>();

    /// <summary>
    /// The name used in the API, e.g. <c>caep-interop</c>.
    /// </summary>
    public static string Alias(this ConformanceScenario scenario)
    {
        return scenario switch
        {
            ConformanceScenario.CaepInterop => "caep-interop",
            ConformanceScenario.CreateDelete => "create-delete",
            ConformanceScenario.StreamManagement => "stream-management",
            ConformanceScenario.StatusUpdate => "status-update",
            ConformanceScenario.RemoveSubject => "remove-subject",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    /// <summary>
    /// The scenario of an alias (<c>caep-interop</c>) or name (<c>CaepInterop</c>), case-insensitive.
    /// </summary>
    /// <exception cref="ArgumentException">if no scenario has the alias</exception>
    public static ConformanceScenario FromAlias(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        foreach (ConformanceScenario scenario in All)
        {
            if (string.Equals(scenario.Alias(), alias, StringComparison.OrdinalIgnoreCase) || string.Equals(scenario.ToString(), alias, StringComparison.OrdinalIgnoreCase))
            {
                return scenario;
            }
        }
        throw new ArgumentException($"Unknown scenario '{alias}'", nameof(alias));
    }

    /// <summary>
    /// The scenario the receiver plays for a test module of the suite, e.g.
    /// <c>openid-ssf-receiver-stream-create-delete</c>.
    /// </summary>
    public static ConformanceScenario ForModule(string moduleName)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        return ModuleScenarios.TryGetValue(moduleName, out ConformanceScenario scenario) ? scenario : ConformanceScenario.CaepInterop;
    }
}
