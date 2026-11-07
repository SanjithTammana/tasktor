namespace Tasktor.Hosts.Common.Render;

/// <summary>
/// A viewer's renderer (A D-A5's eighth seam, owned by U4): fitted cards in, the host's output out. Implementations:
/// <c>TextRenderer</c> (CLI), <c>AdaptiveCardRenderer</c> (widget), <c>FlyoutRenderer</c> (WinUI).
/// </summary>
public interface IBlockRenderer<out TOutput>
{
    TOutput Render(FittedCard card, BindingResolver resolver);
}
