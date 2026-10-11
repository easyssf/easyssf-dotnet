namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// A clock tests move by hand.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock()
        : this(DateTimeOffset.UtcNow)
    {
    }

    public TestClock(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan duration)
    {
        _now += duration;
    }
}
