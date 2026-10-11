using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using EasySsf.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// Runs a <see cref="ConformancePlan">plan</see> of the OpenID conformance suite against a receiver
/// under test: creates the plan in the suite and runs a module of it at a time, playing the
/// <see cref="ConformanceScenario">scenario</see> the module expects with the
/// <see cref="ConformanceRunner"/> of the receiver under test. A test class creates one per plan,
/// starts its receiver on <see cref="ReceiverPort"/> with TLS from <see cref="ReceiverCertificate"/>,
/// trusting the <see cref="ConformanceSuite.Certificate"/>, reachable by the suite at
/// <see cref="ReceiverPushUrl"/>, and asserts the <see cref="ConformanceModuleOutcome"/> of every
/// module.
/// </summary>
public sealed class ConformancePlanRunner
{
    private static readonly TimeSpan ModuleTimeout = TimeSpan.FromMinutes(8);

    private readonly ILogger _logger;

    private ConformancePlanRunner(ConformanceSuite suite, ConformancePlan plan, ConformanceApiClient.Plan createdPlan, string alias, ILogger logger)
    {
        Suite = suite;
        Plan = plan;
        CreatedPlan = createdPlan;
        Alias = alias;
        _logger = logger;
    }

    public ConformanceSuite Suite { get; }

    public ConformancePlan Plan { get; }

    /// <summary>
    /// The plan as created in the suite, with its modules.
    /// </summary>
    public ConformanceApiClient.Plan CreatedPlan { get; }

    /// <summary>
    /// The alias of the test instance, from the suite configuration.
    /// </summary>
    public string Alias { get; }

    /// <summary>
    /// The issuer of the emulated transmitter.
    /// </summary>
    public string Issuer => Suite.Issuer(Alias);

    /// <summary>
    /// The delivery method of the plan's variant.
    /// </summary>
    public SsfDeliveryMethod DeliveryMethod => Plan.DeliveryMethod();

    /// <summary>
    /// The modules of the plan, in the order of the suite.
    /// </summary>
    public IReadOnlyList<ConformanceApiClient.PlanModule> Modules => CreatedPlan.Modules;

    /// <summary>
    /// The port the receiver under test listens on.
    /// </summary>
    public static int ReceiverPort => ConformanceSettings.ReceiverPort;

    /// <summary>
    /// The URL under which the suite reaches the push endpoint of the receiver under test.
    /// </summary>
    public static Uri ReceiverPushUrl(ConformanceSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return ConformanceSettings.ReceiverPushUrl ?? new Uri($"https://{suite.SystemUnderTestHost}:{ReceiverPort}/ssf/push");
    }

    /// <summary>
    /// The TLS certificate of the receiver under test with its private key, for its server.
    /// </summary>
    public static X509Certificate2 ReceiverCertificate()
    {
        return ConformanceHttpClients.LoadCertificateWithKey(ConformanceResources.Read(ConformanceResources.ReceiverCertificate),
            ConformanceResources.Read(ConformanceResources.ReceiverPrivateKey));
    }

    /// <summary>
    /// Creates the plan in the suite.
    /// </summary>
    public static async Task<ConformancePlanRunner> CreateAsync(ConformanceSuite suite, ConformancePlan plan, ILogger<ConformancePlanRunner>? logger = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suite);
        JsonObject config = plan.SuiteConfig();
        string alias = config["alias"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new InvalidOperationException("No alias in " + plan.ConfigFile());
        }
        ConformanceApiClient.Plan created = await suite.Client.CreatePlanAsync(plan.PlanName(), plan.Variant(), config, cancellationToken).ConfigureAwait(false);
        ILogger log = logger ?? NullLogger<ConformancePlanRunner>.Instance;
        log.LogInformation("Created plan {Plan} {Variant} with {Count} modules: {Url}", plan.PlanName(), Variant(plan.Variant()), created.Modules.Count, created.Url);
        return new ConformancePlanRunner(suite, plan, created, alias, log);
    }

    /// <summary>
    /// Runs one module: starts it in the suite, plays the scenario it expects with the runner of the
    /// receiver under test, waits for both to end and reads the result.
    /// </summary>
    public async Task<ConformanceModuleOutcome> RunModuleAsync(ConformanceApiClient.PlanModule module, ConformanceRunner runner,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(runner);
        ConformanceModuleExpectation expectation = ConformanceModuleExpectation.ForModule(module.Name);
        string moduleId = await Suite.Client.StartModuleAsync(CreatedPlan, module, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Module {Module} {Id} is waiting for the receiver: {Url}", module, moduleId, new Uri(Suite.BaseUri, "log-detail.html?log=" + moduleId));
        ConformanceRun run = runner.Start(expectation.Scenario, Issuer, DeliveryMethod, expectation.IdleTimeout);
        await run.Completion.WaitAsync(ModuleTimeout, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Receiver run {Run} ended with {Status}:\n{Log}", run.Id, run.Status, ReceiverLog(run));
        ConformanceModuleResult result = await Suite.Client.AwaitResultAsync(CreatedPlan, module, moduleId, ModuleTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.FinishedWith("PASSED"))
        {
            _logger.LogError("Full log of the failed module {Module}:\n{Log}", module, result.Logs);
        }
        return new ConformanceModuleOutcome(module, expectation, result, run);
    }

    /// <summary>
    /// The log of a run as text, one line per entry.
    /// </summary>
    public static string ReceiverLog(ConformanceRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        StringBuilder log = new();
        foreach (ConformanceRunLogEntry entry in run.Log)
        {
            log.Append("  ").Append(entry.Time.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(entry.Message).Append('\n');
        }
        return log.ToString();
    }

    private static string Variant(IReadOnlyDictionary<string, string> variant)
    {
        return "{" + string.Join(", ", variant.Select(entry => entry.Key + "=" + entry.Value)) + "}";
    }
}

/// <summary>
/// How a module went: the result the suite reports and the run of the receiver.
/// </summary>
/// <param name="Module">the module</param>
/// <param name="Expectation">what was expected of the receiver</param>
/// <param name="Result">the result the suite reports</param>
/// <param name="Run">the run of the receiver under test</param>
public sealed record ConformanceModuleOutcome(ConformanceApiClient.PlanModule Module, ConformanceModuleExpectation Expectation, ConformanceModuleResult Result,
        ConformanceRun Run)
{
    /// <summary>
    /// Whether the suite reports the module as passed and the run ended as expected.
    /// </summary>
    public bool Passed => Result.FinishedWith("PASSED") && Run.Status == Expectation.RunStatus;

    /// <summary>
    /// The result of the suite with its problems, and the run of the receiver with its log.
    /// </summary>
    public string Describe()
    {
        return Result.Summary() + $"\nReceiver run {Run.Id} ended with {Run.Status} (expected {Expectation.RunStatus}):\n" + ConformancePlanRunner.ReceiverLog(Run);
    }
}
