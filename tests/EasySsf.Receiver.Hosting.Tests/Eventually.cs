namespace EasySsf.Receiver.Hosting.Tests;

/// <summary>
/// Waits for background work.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(5);

    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        await UntilAssertedAsync(() => Assert.True(condition()), timeout);
    }

    public static async Task UntilAssertedAsync(Action assertions, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? Default);
        while (true)
        {
            try
            {
                assertions();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
            }
        }
    }
}
