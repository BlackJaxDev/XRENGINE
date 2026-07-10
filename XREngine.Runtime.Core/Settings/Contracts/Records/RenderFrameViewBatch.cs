using System.Numerics;

namespace XREngine;

public readonly record struct RenderFrameViewBatch(
    ERenderFrameViewBatchKind Kind,
    ulong ViewMask,
    int OutputLayerBase,
    string? DebugName)
{
    public int ViewCount => BitOperations.PopCount(ViewMask);
    public bool IsLayered => Kind is ERenderFrameViewBatchKind.LayeredStereoPair or ERenderFrameViewBatchKind.LayeredViewSet;

    public bool ContainsView(int viewIndex)
        => (uint)viewIndex >= 64u 
            ? throw new ArgumentOutOfRangeException(nameof(viewIndex)) 
            : (ViewMask & (1UL << viewIndex)) != 0UL;
}
