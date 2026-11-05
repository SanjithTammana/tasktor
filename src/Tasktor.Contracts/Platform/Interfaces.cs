using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;

namespace Tasktor.Contracts.Platform;

// The seven interfaces of D-A5 (plus IBlockRenderer, which lives in the viewer hosts). No new interface without a spec.

/// <summary>Time. Faked in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>OS → series. <see cref="Read"/> runs off the logic loop; the frame is posted back.</summary>
public interface IMetricSource
{
    string Name { get; }

    /// <summary>Fill <paramref name="frame"/> with current values. Missing metrics are simply absent (e.g. no GPU → no GPU keys).</summary>
    void Read(MetricFrame frame);
}

/// <summary>OS → table. <see cref="Read"/> runs off the logic loop; malformed input is rejected and counted, never thrown into Core.</summary>
public interface ITableSource<TKey, TRow>
    where TKey : notnull
{
    string Name { get; }

    void Read(ICollection<KeyValuePair<TKey, TRow>> into, ColumnSet columns);
}

/// <summary>OS → events. Sources only enqueue: <c>post</c> is thread-safe and puts the event on the loop's channel.</summary>
public interface IEventSource<T>
{
    string Name { get; }

    void Start(Action<T> post);

    void Stop();
}

/// <summary>Logic → toast.</summary>
public interface INotifier
{
    void Show(ToastRequest toast);

    /// <summary>Withdraw a toast by tag (e.g. plugging back in withdraws A8.2's toast).</summary>
    void Withdraw(string tag);
}

/// <summary>Features and plugins → viewers. Cards are semantic: blocks + hints, never colours or pixels.</summary>
public interface ICardProvider
{
    /// <summary>The provider's current cards (structure; values bind to state).</summary>
    IReadOnlyList<Card> Cards();
}

/// <summary>A toast: ≤ 3 buttons, one tag per rule so a repeat replaces the old toast (U4 D-G7).</summary>
public sealed record ToastRequest
{
    public required string Tag { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public Severity Severity { get; init; }

    public IReadOnlyList<ToastButton> Buttons { get; init; } = [];
}

/// <summary>A toast button: an action request sent with origin <c>toast</c> when clicked.</summary>
public sealed record ToastButton
{
    public required string Label { get; init; }

    public required ActionRequest Request { get; init; }
}
