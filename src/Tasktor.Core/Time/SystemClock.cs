using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Time;

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
