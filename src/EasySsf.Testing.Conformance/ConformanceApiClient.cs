using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasySsf.Testing.Conformance;

/// <summary>
/// The part of the suite's REST API the tests use: creating plans and modules, starting modules
/// and reading their state and log.
/// </summary>
public sealed class ConformanceApiClient
{
    private static readonly HashSet<string> TerminalStates = new(["FINISHED", "INTERRUPTED"], StringComparer.Ordinal);

    private readonly Uri _baseUri;

    private readonly HttpClient _httpClient;

    /// <param name="baseUri">the URL of the suite</param>
    /// <param name="httpClient">a client that trusts the TLS certificate of the suite; owned by the caller</param>
    public ConformanceApiClient(Uri baseUri, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(httpClient);
        _baseUri = baseUri;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Waits until the suite answers its availability check.
    /// </summary>
    public async Task WaitUntilAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve("/api/runner/available")), true,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
                lastFailure = new InvalidOperationException("The conformance suite returned HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                lastFailure = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The conformance suite did not become available at " + _baseUri, lastFailure);
    }

    /// <summary>
    /// Creates a plan and returns its id together with the modules it consists of.
    /// </summary>
    public async Task<Plan> CreatePlanAsync(string planName, IReadOnlyDictionary<string, string> variant, JsonNode config,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planName);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(config);
        string path = "/api/plan?planName=" + Uri.EscapeDataString(planName) + "&variant=" + Uri.EscapeDataString(Json(variant));
        string body = config.ToJsonString();
        JsonElement plan = await ExpectJsonAsync(() => new HttpRequestMessage(HttpMethod.Post, Resolve(path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, HttpStatusCode.Created, true, cancellationToken).ConfigureAwait(false);
        string id = RequiredText(plan, "id");
        List<PlanModule> modules = [];
        if (plan.TryGetProperty("modules", out JsonElement moduleList) && moduleList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement module in moduleList.EnumerateArray())
            {
                modules.Add(new PlanModule(RequiredText(module, "testModule"), StringMap(module, "variant")));
            }
        }
        return new Plan(id, planName, variant, modules, Resolve("/plan-detail.html?plan=" + id));
    }

    /// <summary>
    /// Creates and starts a module of a plan. Returns once the module waits for the receiver under
    /// test or has finished already.
    /// </summary>
    /// <returns>the id of the module in the suite</returns>
    public async Task<string> StartModuleAsync(Plan plan, PlanModule module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(module);
        string path = "/api/runner?test=" + Uri.EscapeDataString(module.Name) + "&plan=" + Uri.EscapeDataString(plan.Id) + "&variant="
            + Uri.EscapeDataString(Json(module.Variant));
        JsonElement created = await ExpectJsonAsync(() => new HttpRequestMessage(HttpMethod.Post, Resolve(path)), HttpStatusCode.Created, true,
            cancellationToken).ConfigureAwait(false);
        string id = RequiredText(created, "id");
        JsonElement info = await WaitForStateAsync(id, ["CONFIGURED", "WAITING"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (Text(info, "status") == "CONFIGURED")
        {
            // not replayed, as a lost response would start the module a second time
            await ExpectJsonAsync(() => new HttpRequestMessage(HttpMethod.Post, Resolve("/api/runner/" + id)), HttpStatusCode.OK, false, cancellationToken)
                .ConfigureAwait(false);
            await WaitForStateAsync(id, ["WAITING"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        return id;
    }

    /// <summary>
    /// Waits until the module has ended and reads its result and log.
    /// </summary>
    public async Task<ConformanceModuleResult> AwaitResultAsync(Plan plan, PlanModule module, string moduleId, TimeSpan timeout,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(module);
        JsonElement info = await WaitForStateAsync(moduleId, [], timeout, cancellationToken).ConfigureAwait(false);
        JsonElement logs = await ExpectJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve("/api/log/" + moduleId)), HttpStatusCode.OK, true,
            cancellationToken).ConfigureAwait(false);
        return new ConformanceModuleResult(plan, module, moduleId, Text(info, "status") ?? "", Text(info, "result") ?? "UNKNOWN", logs,
            Resolve("/log-detail.html?log=" + moduleId));
    }

    /// <summary>
    /// The state of a module as the suite reports it.
    /// </summary>
    public Task<JsonElement> InfoAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return ExpectJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve("/api/info/" + moduleId)), HttpStatusCode.OK, true, cancellationToken);
    }

    /// <summary>
    /// Waits until the module is in one of the states or in a terminal one.
    /// </summary>
    private async Task<JsonElement> WaitForStateAsync(string moduleId, IReadOnlyCollection<string> states, TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        JsonElement? lastInfo = null;
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                JsonElement info = await InfoAsync(moduleId, cancellationToken).ConfigureAwait(false);
                lastInfo = info;
                string status = Text(info, "status") ?? "";
                if (states.Contains(status) || TerminalStates.Contains(status))
                {
                    return info;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                lastFailure = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Timed out waiting for module {moduleId} to reach [{string.Join(", ", states)}]; last info: {lastInfo}",
            lastFailure);
    }

    private async Task<JsonElement> ExpectJsonAsync(Func<HttpRequestMessage> request, HttpStatusCode expectedStatus, bool replayable,
            CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(request, replayable, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != expectedStatus)
        {
            throw new InvalidOperationException($"Conformance suite API {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}"
                + $" returned HTTP {(int)response.StatusCode}: {body}");
        }
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The suite occasionally drops a connection, so transient failures of replayable requests are
    /// retried.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, bool replayable, CancellationToken cancellationToken)
    {
        int attempts = replayable ? 4 : 1;
        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage message = request();
            message.Headers.Accept.ParseAdd("application/json");
            try
            {
                return await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= attempts)
                {
                    throw new InvalidOperationException("Conformance suite API request failed: " + message.RequestUri, ex);
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Uri Resolve(string path)
    {
        return new Uri(_baseUri, path);
    }

    private static string Json(IReadOnlyDictionary<string, string> map)
    {
        JsonObject json = [];
        foreach ((string key, string value) in map)
        {
            json[key] = value;
        }
        return json.ToJsonString();
    }

    private static IReadOnlyDictionary<string, string> StringMap(JsonElement node, string property)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (node.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty member in value.EnumerateObject())
            {
                map[member.Name] = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString()! : member.Value.ToString();
            }
        }
        return map;
    }

    private static string RequiredText(JsonElement node, string field)
    {
        string? value = Text(node, field);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Conformance suite API response lacks '{field}': {node}");
        }
        return value;
    }

    internal static string? Text(JsonElement node, string field)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(field, out JsonElement value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.ToString(),
            };
        }
        return null;
    }

    /// <summary>
    /// A created plan.
    /// </summary>
    /// <param name="Id">the id of the plan in the suite</param>
    /// <param name="Name">the name of the plan, e.g. <c>openid-ssf-receiver-test-plan</c></param>
    /// <param name="Variant">the variant the plan was created with</param>
    /// <param name="Modules">the modules of the plan, in the order of the suite</param>
    /// <param name="Url">the suite's page of the plan</param>
    public sealed record Plan(string Id, string Name, IReadOnlyDictionary<string, string> Variant, IReadOnlyList<PlanModule> Modules, Uri Url);

    /// <summary>
    /// A module of a plan with the variant the plan assigns to it.
    /// </summary>
    public sealed record PlanModule(string Name, IReadOnlyDictionary<string, string> Variant)
    {
        public override string ToString()
        {
            return (Variant.Count == 0) ? Name : Name + " {" + string.Join(", ", Variant.Select(entry => entry.Key + "=" + entry.Value)) + "}";
        }
    }
}
