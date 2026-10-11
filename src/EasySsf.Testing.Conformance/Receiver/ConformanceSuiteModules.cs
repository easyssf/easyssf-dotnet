using System.Text.Json;
using System.Text.RegularExpressions;

using EasySsf.Core;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// Looks up the test modules running in the conformance suite, so that a run can be started for
/// the module that waits for the receiver without knowing its name. The suite is the one the
/// transmitter issuer belongs to: a test instance is <c>&lt;suite&gt;/test/a/&lt;alias&gt;</c>. Its API
/// (<c>/api/runner/running</c>, <c>/api/info/&lt;id&gt;</c>) needs no login with the suite's
/// <c>dev</c> profile, as the local suite runs it.
/// </summary>
public sealed partial class ConformanceSuiteModules
{
    /// <summary>
    /// The status of a module that waits for the receiver to act.
    /// </summary>
    public const string Waiting = "WAITING";

    private readonly ConformanceReceiverSettings _settings;

    private readonly HttpClient _httpClient;

    /// <param name="settings">the settings of the receiver under test, for the configured issuer</param>
    /// <param name="httpClient">calls the suite; trusts its certificate</param>
    public ConformanceSuiteModules(ConformanceReceiverSettings settings, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        _settings = settings;
        _httpClient = httpClient;
    }

    /// <summary>
    /// A test module running in the suite.
    /// </summary>
    /// <param name="Id">the id of the module in the suite</param>
    /// <param name="Name">the name of the test module, e.g. <c>openid-ssf-receiver-stream-create-delete</c></param>
    /// <param name="Alias">the alias of the test instance the module runs under</param>
    /// <param name="Status">the status reported by the suite; <see cref="Waiting"/> means it waits for the receiver</param>
    /// <param name="DeliveryMethod">the delivery mode of the module's variant, if it has one</param>
    /// <param name="Issuer">the transmitter issuer the receiver has to use for this module</param>
    /// <param name="Scenario">the scenario the receiver plays for the module</param>
    /// <param name="LogUrl">the suite's log page of the module</param>
    public sealed record Module(string Id, string Name, string Alias, string Status, SsfDeliveryMethod? DeliveryMethod, string Issuer,
            ConformanceScenario Scenario, Uri LogUrl)
    {
        public bool IsWaiting => Status == Waiting;
    }

    /// <summary>
    /// The suite and the alias a transmitter issuer refers to.
    /// </summary>
    /// <param name="Suite">the URL of the suite, with a trailing slash</param>
    /// <param name="Alias">the alias of the test instance</param>
    public sealed record TestInstance(Uri Suite, string Alias)
    {
        /// <summary>
        /// Parses the issuer of a test instance, <c>&lt;suite&gt;/test/a/&lt;alias&gt;</c>.
        /// </summary>
        /// <exception cref="ArgumentException">if the issuer is not a test instance of a suite</exception>
        public static TestInstance Parse(string issuer)
        {
            ArgumentNullException.ThrowIfNull(issuer);
            Match match = TestInstancePattern().Match(issuer.Trim());
            if (!match.Success)
            {
                throw new ArgumentException($"The transmitter issuer '{issuer}' is not a test instance of the conformance suite (<suite>/test/a/<alias>)",
                    nameof(issuer));
            }
            return new TestInstance(new Uri(match.Groups["suite"].Value + "/"), match.Groups["alias"].Value);
        }

        /// <summary>
        /// The issuer of another test instance of the same suite.
        /// </summary>
        public string Issuer(string alias)
        {
            return new Uri(Suite, "test/a/" + alias).ToString();
        }
    }

    /// <summary>
    /// The test instance of the given issuer, or of the configured one (<c>Transmitter:Issuer</c>)
    /// if it is <see langword="null"/>.
    /// </summary>
    public TestInstance TestInstanceOf(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            issuer = _settings.Transmitter.Issuer;
            if (string.IsNullOrWhiteSpace(issuer))
            {
                throw new ArgumentException("Transmitter:Issuer must be set when no issuer is given", nameof(issuer));
            }
        }
        return TestInstance.Parse(issuer);
    }

    /// <summary>
    /// All modules currently running in the suite, in the order the suite lists them.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the suite cannot be reached or answers with an error</exception>
    public async Task<IReadOnlyList<Module>> RunningAsync(TestInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        List<Module> modules = [];
        JsonElement ids = await GetAsync(new Uri(instance.Suite, "api/runner/running"), cancellationToken).ConfigureAwait(false);
        if (ids.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement id in ids.EnumerateArray())
            {
                string moduleId = id.ToString();
                JsonElement info = await GetAsync(new Uri(instance.Suite, "api/info/" + moduleId), cancellationToken).ConfigureAwait(false);
                modules.Add(ModuleOf(instance, moduleId, info));
            }
        }
        return modules;
    }

    /// <summary>
    /// The modules waiting for the receiver that a run should be started for: the ones running
    /// under the alias of the test instance, or, if there is none, all waiting modules of the suite
    /// (another alias than configured may have been used to create the plan).
    /// </summary>
    public async Task<IReadOnlyList<Module>> WaitingAsync(TestInstance instance, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Module> waiting = (await RunningAsync(instance, cancellationToken).ConfigureAwait(false)).Where(module => module.IsWaiting).ToList();
        IReadOnlyList<Module> ofInstance = waiting.Where(module => module.Alias == instance.Alias).ToList();
        return (ofInstance.Count == 0) ? waiting : ofInstance;
    }

    private static Module ModuleOf(TestInstance instance, string id, JsonElement info)
    {
        string name = ConformanceApiClient.Text(info, "testName") ?? "";
        string alias = ConformanceApiClient.Text(info, "alias") ?? instance.Alias;
        string? deliveryMode = (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("variant", out JsonElement variant))
            ? ConformanceApiClient.Text(variant, ConformancePlans.DeliveryModeVariant) : null;
        SsfDeliveryMethod? deliveryMethod = deliveryMode is null ? null : Enum.Parse<SsfDeliveryMethod>(deliveryMode, ignoreCase: true);
        return new Module(id, name, alias, ConformanceApiClient.Text(info, "status") ?? "", deliveryMethod, instance.Issuer(alias),
            ConformanceScenarios.ForModule(name), new Uri(instance.Suite, "log-detail.html?log=" + id));
    }

    private async Task<JsonElement> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("The conformance suite could not be reached at " + uri, ex);
        }
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The conformance suite API {uri} returned HTTP {(int)response.StatusCode}: {body}");
            }
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }

    [GeneratedRegex("^(?<suite>https?://.+?)/test/a/(?<alias>[^/?#]+)/?$")]
    private static partial Regex TestInstancePattern();
}
