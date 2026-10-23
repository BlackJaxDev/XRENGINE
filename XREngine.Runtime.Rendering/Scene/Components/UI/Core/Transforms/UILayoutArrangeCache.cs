using System.Threading;

namespace XREngine.Rendering.UI;

/// <summary>Read-only accepted pages used to skip unchanged arrangement subtrees.</summary>
internal sealed class UILayoutArrangeCache(
    UILayoutSnapshot current, UILayoutSnapshot? previous, UILayoutNodeOutput[]? previousOutputs,
    CancellationToken cancellation)
{
    public UILayoutSnapshot Current { get; } = current;
    public UILayoutSnapshot? Previous { get; } = previous;
    public UILayoutNodeOutput[]? PreviousOutputs { get; } = previousOutputs;
    public CancellationToken Cancellation { get; } = cancellation;
}
