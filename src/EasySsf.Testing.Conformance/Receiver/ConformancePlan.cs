using System.Text.Json.Nodes;

using EasySsf.Core;

namespace EasySsf.Testing.Conformance.Receiver;

/// <summary>
/// The receiver test plans of the OpenID conformance suite. All of them use
/// <c>ssf_auth_mode=dynamic</c> and <c>client_auth_type=client_secret_basic</c>, see
/// <see cref="ConformancePlans"/> for the plan name, the variant and the configuration of each.
/// </summary>
public enum ConformancePlan
{
    /// <summary>
    /// <c>openid-ssf-receiver-test-plan</c> with push delivery.
    /// </summary>
    Ssf10Push,

    /// <summary>
    /// <c>openid-ssf-receiver-test-plan</c> with poll delivery.
    /// </summary>
    Ssf10Poll,

    /// <summary>
    /// <c>openid-ssf-receiver-caep-test-plan</c> with push delivery.
    /// </summary>
    CaepInterop10Push,

    /// <summary>
    /// <c>openid-ssf-receiver-caep-test-plan</c> with poll delivery.
    /// </summary>
    CaepInterop10Poll,
}

/// <summary>
/// The plan name, variant and suite configuration of each <see cref="ConformancePlan"/>.
/// </summary>
public static class ConformancePlans
{
    public const string SsfReceiverPlan = "openid-ssf-receiver-test-plan";

    public const string CaepReceiverPlan = "openid-ssf-receiver-caep-test-plan";

    /// <summary>
    /// The variant key that names the delivery mode.
    /// </summary>
    public const string DeliveryModeVariant = "ssf_delivery_mode";

    public static IReadOnlyList<ConformancePlan> All { get; } = Enum.GetValues<ConformancePlan>();

    /// <summary>
    /// The name of the plan in the suite.
    /// </summary>
    public static string PlanName(this ConformancePlan plan)
    {
        return plan switch
        {
            ConformancePlan.Ssf10Push or ConformancePlan.Ssf10Poll => SsfReceiverPlan,
            ConformancePlan.CaepInterop10Push or ConformancePlan.CaepInterop10Poll => CaepReceiverPlan,
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, null),
        };
    }

    /// <summary>
    /// The delivery method of the plan's variant.
    /// </summary>
    public static SsfDeliveryMethod DeliveryMethod(this ConformancePlan plan)
    {
        return plan switch
        {
            ConformancePlan.Ssf10Push or ConformancePlan.CaepInterop10Push => SsfDeliveryMethod.Push,
            ConformancePlan.Ssf10Poll or ConformancePlan.CaepInterop10Poll => SsfDeliveryMethod.Poll,
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, null),
        };
    }

    /// <summary>
    /// The variant the plan is created with.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variant(this ConformancePlan plan)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeliveryModeVariant] = plan.DeliveryMethod() == SsfDeliveryMethod.Push ? "push" : "poll",
            ["ssf_auth_mode"] = "dynamic",
            ["client_auth_type"] = "client_secret_basic",
        };
    }

    /// <summary>
    /// The name of the suite configuration file, in <c>suite-config/</c> of this assembly.
    /// </summary>
    public static string ConfigFile(this ConformancePlan plan)
    {
        return plan switch
        {
            ConformancePlan.Ssf10Push => "receiver-ssf1.0-push.json",
            ConformancePlan.Ssf10Poll => "receiver-ssf1.0-poll.json",
            ConformancePlan.CaepInterop10Push => "receiver-ssf1.0-caepiop1.0-push.json",
            ConformancePlan.CaepInterop10Poll => "receiver-ssf1.0-caepiop1.0-poll.json",
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, null),
        };
    }

    /// <summary>
    /// The test modules of the plan in the suite release <see cref="ConformanceSettings.DefaultSuiteVersion"/>,
    /// in the order of the suite. Known ahead so that a test runner lists one case per module without
    /// asking the suite; <see cref="ConformancePlanRunner.Modules"/> is what the suite in use has.
    /// </summary>
    public static IReadOnlyList<string> KnownModules(this ConformancePlan plan)
    {
        return plan.PlanName() == CaepReceiverPlan ? CaepReceiverModules : SsfReceiverModules;
    }

    private static readonly IReadOnlyList<string> SsfReceiverModules =
    [
        "openid-ssf-receiver-happypath",
        "openid-ssf-receiver-stream-create-delete",
        "openid-ssf-receiver-stream-status-update",
        "openid-ssf-receiver-stream-verification",
        "openid-ssf-receiver-unsolicited-stream-verification",
        "openid-ssf-receiver-stream-supported-events",
        "openid-ssf-receiver-invalid-set-rejection",
        "openid-ssf-receiver-verification-wrong-state",
        "openid-ssf-receiver-verification-wrong-subject",
        "openid-ssf-receiver-stream-issuer-mismatch",
        "openid-ssf-receiver-transmitter-initiated-status-change",
        "openid-ssf-receiver-verification-behind-queued-events",
        "openid-ssf-receiver-redelivered-set",
        "openid-ssf-receiver-removed-subject-event",
        "openid-ssf-receiver-access-token-expiry",
    ];

    private static readonly IReadOnlyList<string> CaepReceiverModules =
    [
        "openid-ssf-receiver-stream-create-delete",
        "openid-ssf-receiver-stream-verification",
        "openid-ssf-receiver-unsolicited-stream-verification",
        "openid-ssf-receiver-stream-supported-events",
        "openid-ssf-receiver-invalid-set-rejection",
        "openid-ssf-receiver-verification-wrong-state",
        "openid-ssf-receiver-verification-wrong-subject",
        "openid-ssf-receiver-stream-issuer-mismatch",
        "openid-ssf-receiver-verification-behind-queued-events",
        "openid-ssf-receiver-redelivered-set",
        "openid-ssf-receiver-access-token-expiry",
        "openid-ssf-receiver-stream-caep-interop",
    ];

    /// <summary>
    /// The suite configuration the plan is created with: the alias of the test instance, the
    /// stream audience, the valid subjects and the client.
    /// </summary>
    public static JsonObject SuiteConfig(this ConformancePlan plan)
    {
        return JsonNode.Parse(ConformanceResources.Read("suite-config/" + plan.ConfigFile())) as JsonObject
            ?? throw new InvalidOperationException("The suite configuration " + plan.ConfigFile() + " is not a JSON object");
    }
}
