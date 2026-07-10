namespace XREngine;

public readonly record struct RvcViewSetDiagnostics(
    int ViewCount,
    bool IsQuadViewSet,
    ulong TotalPixelCount,
    ERvcFallbackReason FallbackReason)
{
    public static RvcViewSetDiagnostics FromViewSet(in RenderFrameViewSet viewSet, ERvcFallbackReason fallbackReason = ERvcFallbackReason.None)
    {
        ulong pixels = 0UL;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            pixels += (ulong)view.ViewRect.Width * view.ViewRect.Height;
        }

        return new(viewSet.ViewCount, viewSet.IsQuadViewSet, pixels, fallbackReason);
    }
}
