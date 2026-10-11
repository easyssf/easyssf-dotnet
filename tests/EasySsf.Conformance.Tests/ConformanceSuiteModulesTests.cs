using System.Net;

using EasySsf.Core;
using EasySsf.Testing.Conformance.Receiver;

namespace EasySsf.Conformance.Tests;

public sealed class ConformanceSuiteModulesTests
{
    private const string Suite = "https://localhost.emobix.co.uk:8443/";

    private const string Issuer = Suite + "test/a/easyssf-receiver-ssf1_0-caepiop1_0-push";

    private readonly ConformanceReceiverSettings _settings = new();

    [Fact]
    public void ParsesTheTestInstanceOfTheIssuer()
    {
        ConformanceSuiteModules.TestInstance instance = ConformanceSuiteModules.TestInstance.Parse(Issuer + "/");
        Assert.Equal(new Uri(Suite), instance.Suite);
        Assert.Equal("easyssf-receiver-ssf1_0-caepiop1_0-push", instance.Alias);
        Assert.Equal(Suite + "test/a/other", instance.Issuer("other"));

        Assert.Equal(new Uri("https://suite.example.com/suite/"), ConformanceSuiteModules.TestInstance.Parse("https://suite.example.com/suite/test/a/alias").Suite);
        Assert.Throws<ArgumentException>(() => ConformanceSuiteModules.TestInstance.Parse("https://suite.example.com/"));
    }

    [Fact]
    public void UsesTheConfiguredIssuerByDefault()
    {
        _settings.Transmitter.Issuer = Issuer;
        ConformanceSuiteModules modules = new(_settings, SuiteApi([]));
        Assert.Equal("easyssf-receiver-ssf1_0-caepiop1_0-push", modules.TestInstanceOf(null).Alias);
        Assert.Equal("other", modules.TestInstanceOf(Suite + "test/a/other").Alias);
        Assert.Throws<ArgumentException>(() => new ConformanceSuiteModules(new ConformanceReceiverSettings(), SuiteApi([])).TestInstanceOf(null));
    }

    [Fact]
    public async Task ListsTheRunningModulesWithTheirScenario()
    {
        ConformanceSuiteModules modules = new(_settings, SuiteApi(new Dictionary<string, string>
        {
            ["api/runner/running"] = "[\"abc\",\"def\"]",
            ["api/info/abc"] = Info("openid-ssf-receiver-stream-create-delete", "easyssf-receiver-ssf1_0-caepiop1_0-push", "WAITING", "push"),
            ["api/info/def"] = Info("openid-ssf-receiver-happypath", "other", "RUNNING", "poll"),
        }));

        IReadOnlyList<ConformanceSuiteModules.Module> running = await modules.RunningAsync(ConformanceSuiteModules.TestInstance.Parse(Issuer));
        Assert.Equal(2, running.Count);
        ConformanceSuiteModules.Module first = running[0];
        Assert.Equal("abc", first.Id);
        Assert.Equal("openid-ssf-receiver-stream-create-delete", first.Name);
        Assert.Equal("easyssf-receiver-ssf1_0-caepiop1_0-push", first.Alias);
        Assert.Equal("WAITING", first.Status);
        Assert.True(first.IsWaiting);
        Assert.Equal(SsfDeliveryMethod.Push, first.DeliveryMethod);
        Assert.Equal(Issuer, first.Issuer);
        Assert.Equal(ConformanceScenario.CreateDelete, first.Scenario);
        Assert.Equal(new Uri(Suite + "log-detail.html?log=abc"), first.LogUrl);
        ConformanceSuiteModules.Module second = running[1];
        Assert.Equal(SsfDeliveryMethod.Poll, second.DeliveryMethod);
        Assert.Equal(Suite + "test/a/other", second.Issuer);
        Assert.Equal(ConformanceScenario.StreamManagement, second.Scenario);
    }

    [Fact]
    public async Task PrefersTheWaitingModulesOfTheConfiguredAlias()
    {
        ConformanceSuiteModules modules = new(_settings, SuiteApi(new Dictionary<string, string>
        {
            ["api/runner/running"] = "[\"abc\",\"def\",\"ghi\"]",
            ["api/info/abc"] = Info("openid-ssf-receiver-happypath", "other", "WAITING", "push"),
            ["api/info/def"] = Info("openid-ssf-receiver-stream-create-delete", "easyssf-receiver-ssf1_0-caepiop1_0-push", "WAITING", "push"),
            ["api/info/ghi"] = Info("openid-ssf-receiver-removed-subject-event", "easyssf-receiver-ssf1_0-caepiop1_0-push", "RUNNING", "push"),
        }));
        ConformanceSuiteModules.TestInstance instance = ConformanceSuiteModules.TestInstance.Parse(Issuer);

        Assert.Equal(["def"], (await modules.WaitingAsync(instance)).Select(module => module.Id));
        // without a module under the configured alias, any waiting module will do
        Assert.Equal(["abc", "def"], (await modules.WaitingAsync(new ConformanceSuiteModules.TestInstance(new Uri(Suite), "unused"))).Select(module => module.Id));
    }

    [Fact]
    public async Task ReportsAnUnreachableSuite()
    {
        ConformanceSuiteModules modules = new(_settings, SuiteApi([]));
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => modules.RunningAsync(ConformanceSuiteModules.TestInstance.Parse(Issuer)));
        Assert.Contains("returned HTTP 404", ex.Message);
    }

    private static string Info(string testName, string alias, string status, string deliveryMode)
    {
        return $$$"""{"testName":"{{{testName}}}","alias":"{{{alias}}}","status":"{{{status}}}","variant":{"ssf_delivery_mode":"{{{deliveryMode}}}"}}""";
    }

    /// <summary>
    /// A suite API that answers the given paths (relative to the suite) with the given bodies and
    /// everything else with 404.
    /// </summary>
    private static HttpClient SuiteApi(Dictionary<string, string> responses)
    {
        return new HttpClient(new ScriptedHandler(responses));
    }

    private sealed class ScriptedHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = new Uri(Suite).MakeRelativeUri(request.RequestUri!).ToString();
            HttpResponseMessage response = responses.TryGetValue(path, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found: " + path) };
            return Task.FromResult(response);
        }
    }
}
