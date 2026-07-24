using XREngine.Data.Geometry;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering.Physics.DebugVisualization;

/// <summary>
/// World-owned render-thread consumer for backend-neutral physics debug frames.
/// A generation is uploaded at most once and is reused by every camera/view.
/// </summary>
public sealed class PhysicsDebugFrameRenderer : IDisposable
{
    private readonly InstancedDebugVisualizer _worldVisualizer = new(depthTested: true)
    {
        UseCompressedBuffers = true,
    };
    private readonly InstancedDebugVisualizer _overlayVisualizer = new(depthTested: false)
    {
        UseCompressedBuffers = true,
    };
    private long _uploadedGeneration = -1;
    private long _lastReportedOverflowGeneration = -1;
    private long _lastReportedDroppedPublications;

    public long UploadedGeneration => _uploadedGeneration;
    public long UploadedFrameCount { get; private set; }
    public long ReusedViewCount { get; private set; }
    public long CulledViewCount { get; private set; }
    public long RenderedViewCount { get; private set; }
    public PhysicsDebugFrameTelemetry LastFrameTelemetry { get; private set; }
    public InstancedDebugVisualizer WorldVisualizer => _worldVisualizer;
    public InstancedDebugVisualizer OverlayVisualizer => _overlayVisualizer;

    public void Render(
        PhysicsDebugFramePublisher publisher,
        PhysicsDebugDepthMode depthMode)
    {
        if (!publisher.TryAcquireLatest(out PhysicsDebugFrameLease lease))
            return;

        using (lease)
        {
            PhysicsDebugFrame frame = lease.Frame;
            if (frame.DepthMode != depthMode)
                return;

            LastFrameTelemetry = frame.Telemetry;
            ReportCapacityFailures(publisher, frame);
            if (IsCulled(frame))
            {
                CulledViewCount++;
                return;
            }

            if (_uploadedGeneration != frame.Generation)
            {
                InstancedDebugVisualizer visualizer = SelectVisualizer(frame.DepthMode);
                visualizer.Upload(frame);
                if (frame.DepthMode == PhysicsDebugDepthMode.DepthTested)
                    _overlayVisualizer.Clear();
                else
                    _worldVisualizer.Clear();
                _uploadedGeneration = frame.Generation;
                UploadedFrameCount++;
            }
            else
            {
                ReusedViewCount++;
            }

            if (frame.IsEmpty)
                return;

            SelectVisualizer(frame.DepthMode).Render();
            RenderedViewCount++;
        }
    }

    private InstancedDebugVisualizer SelectVisualizer(PhysicsDebugDepthMode depthMode)
        => depthMode == PhysicsDebugDepthMode.DepthTested
            ? _worldVisualizer
            : _overlayVisualizer;

    private void ReportCapacityFailures(
        PhysicsDebugFramePublisher publisher,
        in PhysicsDebugFrame frame)
    {
        if (frame.Telemetry.DroppedPrimitiveCount > 0 &&
            _lastReportedOverflowGeneration != frame.Generation)
        {
            _lastReportedOverflowGeneration = frame.Generation;
            XREngine.Debug.RenderingWarningEvery(
                "PhysicsDebugFrame.PrimitiveBudget",
                TimeSpan.FromSeconds(1),
                "[PhysicsDebug] Generation {0} exceeded its diagnostic budget: dropped points={1}, lines={2}, triangles={3}; published bytes={4}.",
                frame.Generation,
                frame.Telemetry.DroppedPointCount,
                frame.Telemetry.DroppedLineCount,
                frame.Telemetry.DroppedTriangleCount,
                frame.Telemetry.PublishedByteCount);
        }

        long droppedPublications = publisher.DroppedPublications;
        if (droppedPublications == _lastReportedDroppedPublications)
            return;

        _lastReportedDroppedPublications = droppedPublications;
        XREngine.Debug.RenderingWarningEvery(
            "PhysicsDebugFrame.PublicationSlots",
            TimeSpan.FromSeconds(1),
            "[PhysicsDebug] All frame slots were pinned; dropped publication count is now {0}.",
            droppedPublications);
    }

    private static bool IsCulled(in PhysicsDebugFrame frame)
    {
        if (!frame.HasBounds || RuntimeEngine.Rendering.State.RenderingCamera is not { } camera)
            return false;

        AABB bounds = new(frame.BoundsMin, frame.BoundsMax);
        return camera.WorldFrustum().Contains(bounds) == EContainment.Disjoint;
    }

    public void Dispose()
    {
        _worldVisualizer.Dispose();
        _overlayVisualizer.Dispose();
    }
}
