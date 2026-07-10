namespace XREngine;

public readonly record struct RvcFrameProfileSnapshot(
    ulong FrameId,
    long PredictedDisplayTime,
    int ViewCount,
    RvcFrameViewDiagnostics View0,
    RvcFrameViewDiagnostics View1,
    RvcFrameViewDiagnostics View2,
    RvcFrameViewDiagnostics View3,
    RvcViewSetDiagnostics ViewSet,
    ERvcFallbackReason FallbackReason,
    string Diagnostic,
    RvcFrameViewProjectionDiagnostics Projection0,
    RvcFrameViewProjectionDiagnostics Projection1,
    RvcFrameViewProjectionDiagnostics Projection2,
    RvcFrameViewProjectionDiagnostics Projection3)
{
    public static RvcFrameProfileSnapshot Empty => default;

    public RvcFrameViewDiagnostics GetView(int index)
        => index switch
        {
            0 => View0,
            1 => View1,
            2 => View2,
            3 => View3,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "RVC frame profiles expose the first four XR views."),
        };

    public RvcFrameViewProjectionDiagnostics GetProjection(int index)
        => index switch
        {
            0 => Projection0,
            1 => Projection1,
            2 => Projection2,
            3 => Projection3,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "RVC frame profiles expose the first four XR views."),
        };

    public static RvcFrameProfileSnapshot Create(
        ulong frameId,
        long predictedDisplayTime,
        ReadOnlySpan<RvcFrameViewDiagnostics> views,
        ERvcFallbackReason fallbackReason,
        string diagnostic)
        => Create(
            frameId,
            predictedDisplayTime,
            views,
            ReadOnlySpan<RvcFrameViewProjectionDiagnostics>.Empty,
            fallbackReason,
            diagnostic);

    public static RvcFrameProfileSnapshot Create(
        ulong frameId,
        long predictedDisplayTime,
        ReadOnlySpan<RvcFrameViewDiagnostics> views,
        ReadOnlySpan<RvcFrameViewProjectionDiagnostics> projections,
        ERvcFallbackReason fallbackReason,
        string diagnostic)
    {
        int viewCount = Math.Min(views.Length, 4);
        RvcFrameViewDiagnostics view0 = viewCount > 0 ? views[0] : default;
        RvcFrameViewDiagnostics view1 = viewCount > 1 ? views[1] : default;
        RvcFrameViewDiagnostics view2 = viewCount > 2 ? views[2] : default;
        RvcFrameViewDiagnostics view3 = viewCount > 3 ? views[3] : default;
        int projectionCount = Math.Min(projections.Length, viewCount);
        RvcFrameViewProjectionDiagnostics projection0 = projectionCount > 0 ? projections[0] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection1 = projectionCount > 1 ? projections[1] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection2 = projectionCount > 2 ? projections[2] : RvcFrameViewProjectionDiagnostics.Empty;
        RvcFrameViewProjectionDiagnostics projection3 = projectionCount > 3 ? projections[3] : RvcFrameViewProjectionDiagnostics.Empty;
        ulong totalPixels = 0UL;
        for (int i = 0; i < viewCount; i++)
            totalPixels += views[i].PixelCount;

        bool quadViewSet =
            viewCount == 4 &&
            view0.ViewKind == EVrOutputViewKind.LeftWide &&
            view1.ViewKind == EVrOutputViewKind.RightWide &&
            view2.ViewKind == EVrOutputViewKind.LeftInset &&
            view3.ViewKind == EVrOutputViewKind.RightInset;

        return new(
            frameId,
            predictedDisplayTime,
            viewCount,
            view0,
            view1,
            view2,
            view3,
            new RvcViewSetDiagnostics(viewCount, quadViewSet, totalPixels, fallbackReason),
            fallbackReason,
            diagnostic,
            projection0,
            projection1,
            projection2,
            projection3);
    }
}
