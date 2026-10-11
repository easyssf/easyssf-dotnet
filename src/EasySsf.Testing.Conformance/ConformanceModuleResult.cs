using System.Text;
using System.Text.Json;

namespace EasySsf.Testing.Conformance;

/// <summary>
/// The outcome of a module run as the suite reports it.
/// </summary>
/// <param name="Plan">the plan the module belongs to</param>
/// <param name="Module">the module</param>
/// <param name="ModuleId">the id of the module's run in the suite</param>
/// <param name="Status">the status of the run, <c>FINISHED</c> when it ended normally</param>
/// <param name="Result">the result, <c>PASSED</c>, <c>WARNING</c>, <c>FAILED</c>, ...</param>
/// <param name="Logs">the log of the suite, an array of entries</param>
/// <param name="Url">the suite's log page of the run</param>
public sealed record ConformanceModuleResult(ConformanceApiClient.Plan Plan, ConformanceApiClient.PlanModule Module, string ModuleId, string Status,
        string Result, JsonElement Logs, Uri Url)
{
    public bool FinishedWith(string expectedResult)
    {
        return Status == "FINISHED" && Result == expectedResult;
    }

    /// <summary>
    /// The failures and warnings the suite logged.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        if (Logs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return Logs.EnumerateArray()
            .Where(entry => ConformanceApiClient.Text(entry, "result") is "FAILURE" or "WARNING")
            .Select(Format)
            .ToList();
    }

    public string Summary()
    {
        StringBuilder summary = new StringBuilder("Conformance module ").Append(Module).Append(" of plan ").Append(Plan.Name).Append(" {")
            .Append(string.Join(", ", Plan.Variant.Select(entry => entry.Key + "=" + entry.Value))).Append("}: status ").Append(Status)
            .Append(", result ").Append(Result).Append(" (").Append(Url).Append(')');
        IReadOnlyList<string> problems = Problems();
        if (problems.Count == 0)
        {
            summary.Append("\n  no failures or warnings logged");
        }
        foreach (string problem in problems.Take(20))
        {
            summary.Append("\n  ").Append(problem);
        }
        return summary.ToString();
    }

    private static string Format(JsonElement entry)
    {
        string source = ConformanceApiClient.Text(entry, "src") ?? ConformanceApiClient.Text(entry, "condition") ?? "";
        string message = ConformanceApiClient.Text(entry, "msg") ?? ConformanceApiClient.Text(entry, "message") ?? ConformanceApiClient.Text(entry, "error") ?? "";
        StringBuilder formatted = new StringBuilder(ConformanceApiClient.Text(entry, "result")).Append(' ').Append(source).Append(": ").Append(message);
        foreach (string detail in new[] { "url", "match", "detail", "requirements" })
        {
            if (entry.TryGetProperty(detail, out JsonElement value) && value.ValueKind is not JsonValueKind.Null)
            {
                formatted.Append(' ').Append(detail).Append('=').Append(value);
            }
        }
        return formatted.ToString();
    }
}
