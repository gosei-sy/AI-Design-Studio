using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Controllable clock. Every time-dependent invariant is tested against this
/// rather than against wall-clock time, so lease expiry and retry backoff are
/// deterministic instead of slow and flaky.
/// </summary>
public sealed class TestClock(DateTimeOffset? start = null) : ISimulatedClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// Attempts to mutate a collection handed out through a read-only interface.
///
/// A correctly wrapped collection either throws <see cref="NotSupportedException"/>
/// or is not castable at all; both are a pass. The failure this detects is a
/// mutation that silently succeeds, which is what happens when an aggregate
/// returns its live backing list.
/// </summary>
internal static class MutationAttempt
{
    public static void TryClear<T>(IReadOnlyList<T> collection)
    {
        try
        {
            (collection as ICollection<T>)?.Clear();
        }
        catch (NotSupportedException)
        {
            // The wrapper refused, which is the point.
        }

        try
        {
            (collection as List<T>)?.Clear();
        }
        catch (NotSupportedException)
        {
        }
    }
}
