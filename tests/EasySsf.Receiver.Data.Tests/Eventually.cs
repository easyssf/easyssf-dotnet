namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// Waits for background work, the way Awaitility does in the Java tests.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Waits until the condition holds, failing after the timeout.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        await UntilAssertedAsync(() => Assert.True(condition()), timeout);
    }

    /// <summary>
    /// Waits until the assertions pass, failing with the last assertion error after the timeout.
    /// </summary>
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

    /// <summary>
    /// Waits until the asynchronous assertions pass, failing with the last assertion error after the
    /// timeout.
    /// </summary>
    public static async Task UntilAssertedAsync(Func<Task> assertions, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? Default);
        while (true)
        {
            try
            {
                await assertions();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
            }
        }
    }

    /// <summary>
    /// Asserts that the condition holds throughout the duration.
    /// </summary>
    public static async Task DuringAsync(Func<bool> condition, TimeSpan duration)
    {
        DateTimeOffset end = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < end)
        {
            Assert.True(condition());
            await Task.Delay(PollInterval);
        }
        Assert.True(condition());
    }
}
