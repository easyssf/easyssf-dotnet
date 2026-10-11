using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Testing.Conformance.Receiver;

namespace EasySsf.Conformance.Tests;

public sealed class ConformancePlansAndScenariosTests
{
    [Fact]
    public void EveryPlanHasAConfigurationWithAnAliasAndTheDeliveryOfItsVariant()
    {
        foreach (ConformancePlan plan in ConformancePlans.All)
        {
            JsonObject config = plan.SuiteConfig();
            string alias = config["alias"]!.GetValue<string>();
            Assert.DoesNotContain('.', alias);
            Assert.EndsWith(plan.DeliveryMethod() == SsfDeliveryMethod.Push ? "-push" : "-poll", alias);
            Assert.Equal(plan.DeliveryMethod() == SsfDeliveryMethod.Push ? "push" : "poll", plan.Variant()[ConformancePlans.DeliveryModeVariant]);
            Assert.Equal("dynamic", plan.Variant()["ssf_auth_mode"]);
            Assert.Equal("easyssf-receiver", config["client"]!["client_id"]!.GetValue<string>());
        }
        Assert.Equal(ConformancePlans.CaepReceiverPlan, ConformancePlan.CaepInterop10Poll.PlanName());
        Assert.Equal(15, ConformancePlan.Ssf10Push.KnownModules().Count);
        Assert.Equal(12, ConformancePlan.CaepInterop10Poll.KnownModules().Count);
        Assert.All(ConformancePlan.Ssf10Poll.KnownModules(), module => Assert.StartsWith("openid-ssf-receiver-", module));
        Assert.Equal(ConformancePlans.SsfReceiverPlan, ConformancePlan.Ssf10Push.PlanName());
    }

    [Fact]
    public void ScenariosHaveAliasesAndModulesTheirScenario()
    {
        foreach (ConformanceScenario scenario in ConformanceScenarios.All)
        {
            Assert.Equal(scenario, ConformanceScenarios.FromAlias(scenario.Alias()));
            Assert.Equal(scenario, ConformanceScenarios.FromAlias(scenario.ToString().ToUpperInvariant()));
        }
        Assert.Equal("caep-interop", ConformanceScenario.CaepInterop.Alias());
        Assert.Throws<ArgumentException>(() => ConformanceScenarios.FromAlias("nope"));
        Assert.Equal(ConformanceScenario.CreateDelete, ConformanceScenarios.ForModule("openid-ssf-receiver-stream-create-delete"));
        Assert.Equal(ConformanceScenario.StreamManagement, ConformanceScenarios.ForModule("openid-ssf-receiver-happypath"));
        Assert.Equal(ConformanceScenario.CaepInterop, ConformanceScenarios.ForModule("openid-ssf-receiver-anything-else"));

        ConformanceModuleExpectation mismatch = ConformanceModuleExpectation.ForModule("openid-ssf-receiver-stream-issuer-mismatch");
        Assert.Equal(ConformanceRunStatus.RefusedStream, mismatch.RunStatus);
        Assert.Equal(ConformanceModuleExpectation.PausingIdleTimeout, ConformanceModuleExpectation.ForModule("openid-ssf-receiver-access-token-expiry").IdleTimeout);
        Assert.Equal(ConformanceRunStatus.Finished, ConformanceModuleExpectation.ForModule("openid-ssf-receiver-happypath").RunStatus);
    }
}
