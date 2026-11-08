using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Deep;

/// <summary>
/// C19.1: names the element under a click ("role: name", e.g. from a UIA hit test, #8). Called off the capture logic
/// thread; the answer joins the click only within <see cref="DeepCapture.ClickTargetBudgetMs"/>. Null: nothing there.
/// </summary>
public interface IClickTargetResolver
{
    Task<Sensitive<string>?> ResolveAsync(int x, int y, CancellationToken cancel);
}
