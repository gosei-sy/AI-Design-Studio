namespace AIDesignStudio.SharedKernel;

/// <summary>
/// Abstracts wall-clock time so that domain invariants involving time are testable.
/// Never call <c>DateTimeOffset.UtcNow</c> directly in domain or application code.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
