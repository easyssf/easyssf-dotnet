namespace EasySsf.Conformance.Tests;

/// <summary>
/// A theory that runs the conformance suite in Docker: skipped unless
/// <c>EASYSSF_CONFORMANCE_TESTS</c> is set, so that a plain <c>dotnet test</c> needs no Docker and
/// does not take minutes per plan.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConformanceTheoryAttribute : TheoryAttribute
{
    public const string Variable = "EASYSSF_CONFORMANCE_TESTS";

    public ConformanceTheoryAttribute()
    {
        if (!Enabled)
        {
            Skip = $"Set {Variable}=true to run the conformance suite in Docker (minutes per plan)";
        }
    }

    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable));
}

/// <summary>
/// A fact that runs the conformance suite in Docker, skipped like a <see cref="ConformanceTheoryAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConformanceFactAttribute : FactAttribute
{
    public ConformanceFactAttribute()
    {
        if (!ConformanceTheoryAttribute.Enabled)
        {
            Skip = $"Set {ConformanceTheoryAttribute.Variable}=true to run the conformance suite in Docker (minutes per plan)";
        }
    }
}
