using XREngine.Rendering.UI;

namespace XREngine.Components;

/// <summary>One reserved canvas surface held until its exact producer retires.</summary>
internal sealed record PendingOffscreenProducer(
    UIPublication Publication, UIRenderSurface Surface, ulong Generation,
    UIReceiptCompletionProbe Completion);
