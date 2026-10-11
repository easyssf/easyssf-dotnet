using System.Text.Json;

namespace EasySsf.Core.Event;

/// <summary>
/// Reads the <c>event_timestamp</c> claim of an event payload. CAEP 1.0 (section 2) and RISC
/// 1.0 (section 2.7) define it as a JSON number of seconds since the epoch.
/// </summary>
public static class SsfEventTimestamps
{
    private const long MillisecondsThreshold = 100_000_000_000L;

    /// <param name="value">the value of an <c>event_timestamp</c> claim</param>
    /// <returns>
    /// the instant, <see langword="null"/> if the value is not a number. A value that is clearly
    /// milliseconds is read as such: some transmitters send milliseconds.
    /// </returns>
    public static DateTimeOffset? From(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } timestamp)
        {
            return null;
        }
        long number;
        if (!timestamp.TryGetInt64(out number))
        {
            if (!timestamp.TryGetDouble(out double fraction) || !double.IsFinite(fraction))
            {
                return null;
            }
            number = (long)fraction;
        }
        // CAEP defines seconds since epoch, some transmitters send milliseconds
        return (number > MillisecondsThreshold) ? DateTimeOffset.FromUnixTimeMilliseconds(number)
            : DateTimeOffset.FromUnixTimeSeconds(number);
    }
}
