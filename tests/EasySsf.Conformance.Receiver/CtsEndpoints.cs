using System.Text.Json.Nodes;

using EasySsf.Core;
using EasySsf.Receiver.Push;
using EasySsf.Testing.Conformance.Receiver;

namespace EasySsf.Conformance.Receiver;

/// <summary>
/// Starts test runs and receives the SETs the suite pushes.
/// </summary>
public static class CtsEndpoints
{
    public static void MapCtsEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/", () => Results.Text(Usage()));
        app.MapPost("/cts/runs", StartAsync);
        app.MapPost("/cts/runs/auto", StartForWaitingModuleAsync);
        app.MapGet("/cts/suite/modules", SuiteModulesAsync);
        app.MapGet("/cts/runs/current", Current);
        app.MapPost("/cts/runs/current/stop", StopAsync);
        app.MapPost("/ssf/push", PushAsync);
    }

    private static string Usage()
    {
        return $"""
            easyssf receiver under test for the OpenID conformance suite

            POST /cts/runs/auto                  start the run the test module waiting in the suite expects
                 ?issuer=<issuer>                optional: a test instance of the suite, default Cts:Transmitter:Issuer
            GET  /cts/suite/modules              the test modules running in the suite and the scenario each gets
            POST /cts/runs?scenario=<scenario>   start a run (stops the current one), scenarios: [{string.Join(", ", ConformanceScenarios.All.Select(s => s.Alias()))}]
                 &issuer=<issuer>                optional: the test instance of the suite, default Cts:Transmitter:Issuer
                 &delivery=push|poll             optional: default Cts:Delivery:Method
            GET  /cts/runs/current               what the current run did so far
            POST /cts/runs/current/stop          stop the current run and delete its stream
            POST /ssf/push                       push endpoint for the suite

            """;
    }

    private static IResult StartAsync(HttpContext context, ConformanceRunner runner)
    {
        string scenarioAlias = context.Request.Query["scenario"].FirstOrDefault() ?? ConformanceScenario.CaepInterop.Alias();
        string? issuer = context.Request.Query["issuer"].FirstOrDefault();
        string? delivery = context.Request.Query["delivery"].FirstOrDefault();
        ConformanceScenario scenario;
        SsfDeliveryMethod? deliveryMethod;
        try
        {
            scenario = ConformanceScenarios.FromAlias(scenarioAlias);
            deliveryMethod = (delivery is null) ? null : Enum.Parse<SsfDeliveryMethod>(delivery, ignoreCase: true);
        }
        catch (ArgumentException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, ex.Message);
        }
        try
        {
            return Json(StatusCodes.Status202Accepted, RunView(runner.Start(scenario, issuer, deliveryMethod)));
        }
        catch (ArgumentException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    /// <summary>
    /// Starts the run for the test module that waits for the receiver in the suite: the scenario
    /// the module expects, against the module's test instance and with the delivery method of its
    /// variant.
    /// </summary>
    private static async Task<IResult> StartForWaitingModuleAsync(HttpContext context, ConformanceRunner runner, ConformanceSuiteModules suiteModules)
    {
        string? issuer = context.Request.Query["issuer"].FirstOrDefault();
        ConformanceSuiteModules.TestInstance instance;
        IReadOnlyList<ConformanceSuiteModules.Module> waiting;
        try
        {
            instance = suiteModules.TestInstanceOf(issuer);
            waiting = await suiteModules.WaitingAsync(instance, context.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(StatusCodes.Status502BadGateway, ex.Message);
        }
        if (waiting.Count == 0)
        {
            return Problem(StatusCodes.Status404NotFound,
                $"No test module is waiting for the receiver in the conformance suite at {instance.Suite}; start one there first");
        }
        if (waiting.Count > 1)
        {
            return Problem(StatusCodes.Status409Conflict, "Several test modules are waiting for the receiver: ["
                + string.Join(", ", waiting.Select(module => module.Name + " (" + module.Issuer + ")")) + "]; pick one with ?issuer=<issuer>");
        }
        ConformanceSuiteModules.Module waitingModule = waiting[0];
        ConformanceRun run = runner.Start(waitingModule.Scenario, waitingModule.Issuer, waitingModule.DeliveryMethod);
        return Json(StatusCodes.Status202Accepted, new JsonObject { ["module"] = ModuleView(waitingModule), ["run"] = RunView(run) });
    }

    private static async Task<IResult> SuiteModulesAsync(HttpContext context, ConformanceSuiteModules suiteModules)
    {
        string? issuer = context.Request.Query["issuer"].FirstOrDefault();
        try
        {
            IReadOnlyList<ConformanceSuiteModules.Module> running = await suiteModules.RunningAsync(suiteModules.TestInstanceOf(issuer), context.RequestAborted);
            return Json(StatusCodes.Status200OK, new JsonArray(running.Select(module => (JsonNode)ModuleView(module)).ToArray()));
        }
        catch (ArgumentException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static IResult Current(ConformanceRunner runner)
    {
        return (runner.Current is { } run) ? Json(StatusCodes.Status200OK, RunView(run)) : Problem(StatusCodes.Status404NotFound, "No run was started yet");
    }

    private static async Task<IResult> StopAsync(ConformanceRunner runner, CancellationToken cancellationToken)
    {
        if (runner.Current is not { } run)
        {
            return Problem(StatusCodes.Status404NotFound, "No run was started yet");
        }
        await run.StopAsync(cancellationToken);
        return Json(StatusCodes.Status200OK, RunView(run));
    }

    private static async Task<IResult> PushAsync(HttpContext context, ConformanceRunner runner)
    {
        ConformanceRun? run = runner.Current is { IsActive: true } active ? active : null;
        if (run is null)
        {
            context.Response.Headers.ContentLanguage = SsfPushResponse.ContentLanguage;
            return Results.Content("{\"err\":\"invalid_request\",\"description\":\"No conformance run is active\"}", SsfPushResponse.ContentType,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        using MemoryStream body = new();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        SsfPushResponse response = await run.PushAsync(context.Request.Headers.Authorization.FirstOrDefault(), body.ToArray(), context.RequestAborted);
        if (response.Body is null)
        {
            return Results.StatusCode(response.Status);
        }
        context.Response.Headers.ContentLanguage = SsfPushResponse.ContentLanguage;
        return Results.Content(response.Body, SsfPushResponse.ContentType, statusCode: response.Status);
    }

    /// <summary>
    /// The current run: status, stream, log, received SETs.
    /// </summary>
    internal static JsonObject RunView(ConformanceRun run)
    {
        return new JsonObject
        {
            ["id"] = run.Id,
            ["scenario"] = run.Scenario.Alias(),
            ["issuer"] = run.Issuer,
            ["delivery"] = run.DeliveryMethod.ToString().ToUpperInvariant(),
            ["status"] = Status(run.Status),
            ["streamId"] = run.StreamId,
            ["startedAt"] = run.StartedAt.ToString("O"),
            ["log"] = new JsonArray(run.Log.Select(entry => (JsonNode)new JsonObject { ["time"] = entry.Time.ToString("O"), ["message"] = entry.Message }).ToArray()),
            ["receivedSets"] = new JsonArray(run.ReceivedSets.Select(set => (JsonNode)new JsonObject
            {
                ["time"] = set.Time.ToString("O"),
                ["jti"] = set.Jti,
                ["events"] = new JsonArray(set.Events.Select(e => (JsonNode)e).ToArray()),
                ["outcome"] = set.Outcome,
            }).ToArray()),
        };
    }

    /// <summary>
    /// The status in the spelling of the Java receiver under test (<c>REFUSED_STREAM</c>), so that
    /// the scripts work against both.
    /// </summary>
    internal static string Status(ConformanceRunStatus status)
    {
        return status switch
        {
            ConformanceRunStatus.RefusedStream => "REFUSED_STREAM",
            _ => status.ToString().ToUpperInvariant(),
        };
    }

    private static JsonObject ModuleView(ConformanceSuiteModules.Module module)
    {
        return new JsonObject
        {
            ["id"] = module.Id,
            ["name"] = module.Name,
            ["alias"] = module.Alias,
            ["status"] = module.Status,
            ["deliveryMethod"] = module.DeliveryMethod?.ToString().ToUpperInvariant(),
            ["issuer"] = module.Issuer,
            ["scenario"] = module.Scenario.Alias(),
            ["logUrl"] = module.LogUrl.ToString(),
        };
    }

    private static IResult Json(int status, JsonNode body)
    {
        return Results.Content(body.ToJsonString(), "application/json", statusCode: status);
    }

    private static IResult Problem(int status, string message)
    {
        return Json(status, new JsonObject { ["status"] = status, ["message"] = message });
    }
}
