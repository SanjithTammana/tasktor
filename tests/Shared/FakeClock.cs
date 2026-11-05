using Tasktor.Contracts.Platform;

namespace Tasktor.Tests.Shared;

/// <summary>A clock tests move by hand.</summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}
