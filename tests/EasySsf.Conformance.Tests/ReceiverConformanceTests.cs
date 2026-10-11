using System.Collections.Concurrent;

using EasySsf.Conformance.Receiver;
using EasySsf.Testing.Conformance;
using EasySsf.Testing.Conformance.Receiver;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasySsf.Conformance.Tests;

/// <summary>
/// The conformance plans against the receiver under test, <see cref="CtsApplication"/>: one test
/// per module of a plan, from the modules the pinned suite release has
/// (<see cref="ConformancePlans.KnownModules"/>), plus one that checks the suite in use has exactly
/// those. Skipped unless <see cref="ConformanceTheoryAttribute.Variable"/> is set, see
/// <see cref="ConformanceTheoryAttribute"/>. The plan classes share one collection, so they run one
/// after another against the one receiver and the one suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConformanceCollection : ICollectionFixture<ConformanceReceiverFixture>
{
    public const string Name = "conformance";
}

/// <summary>
/// The receiver under test, started once for all plans on <see cref="ConformancePlanRunner.ReceiverPort"/>
/// with TLS, trusting the suite's certificate.
/// </summary>
public sealed class ConformanceReceiverFixture : IAsyncLifetime
{
    private WebApplication? _app;

    private string? _certificateFile;

    private string? _keyFile;

    public ConformanceRunner Runner => _app?.Services.GetRequiredService<ConformanceRunner>()
        ?? throw new InvalidOperationException("The receiver under test is not running");

    public async Task InitializeAsync()
    {
        if (!ConformanceTheoryAttribute.Enabled)
        {
            return;
        }
        ConformanceSuite suite = await ConformanceSuite.InstanceAsync();
        _certificateFile = WriteTemp(ConformanceResources.Read(ConformanceResources.ReceiverCertificate));
        _keyFile = WriteTemp(ConformanceResources.Read(ConformanceResources.ReceiverPrivateKey));
        HttpClient transmitterClient = suite.CreateHttpClient();
        _app = CtsApplication.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cts:Server:Url"] = "https://0.0.0.0:" + ConformancePlanRunner.ReceiverPort,
                ["Cts:Server:CertificateFile"] = _certificateFile,
                ["Cts:Server:KeyFile"] = _keyFile,
                ["Cts:Delivery:PushUrl"] = ConformancePlanRunner.ReceiverPushUrl(suite).ToString(),
                ["Cts:Transmitter:Issuer"] = suite.Issuer("easyssf-receiver"),
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            });
            builder.Services.AddSingleton(provider => new ConformanceRunner(
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConformanceReceiverSettings>>().Value, transmitterClient,
                provider.GetRequiredService<ILoggerFactory>()));
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        foreach (string? file in new[] { _certificateFile, _keyFile })
        {
            if (file is not null)
            {
                File.Delete(file);
            }
        }
    }

    private static string WriteTemp(string content)
    {
        string file = Path.Combine(Path.GetTempPath(), "easyssf-cts-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(file, content);
        return file;
    }
}

/// <summary>
/// Runs the modules of a plan; the plan classes name their plan.
/// </summary>
public abstract class ReceiverConformanceTests(ConformanceReceiverFixture receiver)
{
    private static readonly ConcurrentDictionary<ConformancePlan, Lazy<Task<ConformancePlanRunner>>> Runners = new();

    /// <summary>
    /// The modules of the plan, for the member data of the plan classes: the known ones, so that
    /// collecting the tests needs no suite (a filtered <c>dotnet test</c> collects and runs in
    /// different processes).
    /// </summary>
    protected static IEnumerable<object[]> ModulesOf(ConformancePlan plan)
    {
        return plan.KnownModules().Select(module => new object[] { module });
    }

    protected async Task RunModuleAsync(ConformancePlan plan, string module)
    {
        ConformancePlanRunner runner = await RunnerOf(plan);
        ConformanceApiClient.PlanModule planModule = runner.Modules.FirstOrDefault(candidate => candidate.Name == module)
            ?? throw new InvalidOperationException($"The suite's plan {runner.CreatedPlan.Name} has no module {module}; update ConformancePlans.KnownModules"
                + $" to the suite release in use (modules: {string.Join(", ", runner.Modules.Select(m => m.Name))})");
        ConformanceModuleOutcome outcome = await runner.RunModuleAsync(planModule, receiver.Runner);
        Assert.True(outcome.Passed, outcome.Describe());
    }

    /// <summary>
    /// Fails when the suite in use has other modules than the known ones, so that a new module of a
    /// newer suite is not silently left out.
    /// </summary>
    protected async Task AssertKnownModulesAsync(ConformancePlan plan)
    {
        ConformancePlanRunner runner = await RunnerOf(plan);
        Assert.Equal(plan.KnownModules(), runner.Modules.Select(module => module.Name));
    }

    private static Task<ConformancePlanRunner> RunnerOf(ConformancePlan plan)
    {
        return Runners.GetOrAdd(plan, p => new Lazy<Task<ConformancePlanRunner>>(async () =>
        {
            ConformanceSuite suite = await ConformanceSuite.InstanceAsync();
            using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddConsole());
            return await ConformancePlanRunner.CreateAsync(suite, p, loggers.CreateLogger<ConformancePlanRunner>());
        })).Value;
    }
}

[Collection(ConformanceCollection.Name)]
[Trait("Category", "conformance")]
public sealed class Ssf10PushReceiverConformanceTests(ConformanceReceiverFixture receiver) : ReceiverConformanceTests(receiver)
{
    public static IEnumerable<object[]> Modules => ModulesOf(ConformancePlan.Ssf10Push);

    [ConformanceTheory]
    [MemberData(nameof(Modules))]
    public Task Conformance(string module) => RunModuleAsync(ConformancePlan.Ssf10Push, module);

    [ConformanceFact]
    public Task PlanHasTheKnownModules() => AssertKnownModulesAsync(ConformancePlan.Ssf10Push);
}

[Collection(ConformanceCollection.Name)]
[Trait("Category", "conformance")]
public sealed class Ssf10PollReceiverConformanceTests(ConformanceReceiverFixture receiver) : ReceiverConformanceTests(receiver)
{
    public static IEnumerable<object[]> Modules => ModulesOf(ConformancePlan.Ssf10Poll);

    [ConformanceTheory]
    [MemberData(nameof(Modules))]
    public Task Conformance(string module) => RunModuleAsync(ConformancePlan.Ssf10Poll, module);

    [ConformanceFact]
    public Task PlanHasTheKnownModules() => AssertKnownModulesAsync(ConformancePlan.Ssf10Poll);
}

[Collection(ConformanceCollection.Name)]
[Trait("Category", "conformance")]
public sealed class CaepInterop10PushReceiverConformanceTests(ConformanceReceiverFixture receiver) : ReceiverConformanceTests(receiver)
{
    public static IEnumerable<object[]> Modules => ModulesOf(ConformancePlan.CaepInterop10Push);

    [ConformanceTheory]
    [MemberData(nameof(Modules))]
    public Task Conformance(string module) => RunModuleAsync(ConformancePlan.CaepInterop10Push, module);

    [ConformanceFact]
    public Task PlanHasTheKnownModules() => AssertKnownModulesAsync(ConformancePlan.CaepInterop10Push);
}

[Collection(ConformanceCollection.Name)]
[Trait("Category", "conformance")]
public sealed class CaepInterop10PollReceiverConformanceTests(ConformanceReceiverFixture receiver) : ReceiverConformanceTests(receiver)
{
    public static IEnumerable<object[]> Modules => ModulesOf(ConformancePlan.CaepInterop10Poll);

    [ConformanceTheory]
    [MemberData(nameof(Modules))]
    public Task Conformance(string module) => RunModuleAsync(ConformancePlan.CaepInterop10Poll, module);

    [ConformanceFact]
    public Task PlanHasTheKnownModules() => AssertKnownModulesAsync(ConformancePlan.CaepInterop10Poll);
}
