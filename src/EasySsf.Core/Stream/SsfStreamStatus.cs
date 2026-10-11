namespace EasySsf.Core.Stream;

/// <summary>
/// The status of an event stream (SSF 1.0, section 8.1.2).
/// </summary>
/// <param name="StreamId">the identifier of the stream</param>
/// <param name="Status"><c>enabled</c>, <c>paused</c> or <c>disabled</c></param>
/// <param name="Reason">why the stream has that status, may be <see langword="null"/></param>
public sealed record SsfStreamStatus(string? StreamId, string? Status, string? Reason)
{
    public const string Enabled = "enabled";

    public const string Paused = "paused";

    public const string Disabled = "disabled";
}
