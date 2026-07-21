using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    internal void CollectRenderedItems(
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        bool cullWithFrustum,
        Func<IRuntimeCullingCamera>? cullingCameraOverride,
        IVolume? collectionVolumeOverride,
        bool collectMirrors,
        XRRenderPipelineInstance.RenderingState visibilityState)
    {
        IRuntimeCullingCamera? cullingCamera = cullingCameraOverride?.Invoke() ?? camera;
        IVolume? collectionVolume = collectionVolumeOverride
            ?? (cullWithFrustum ? cullingCamera?.WorldFrustum() : null);
        int commandsBefore = commands.GetUpdatingCommandCount();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!TryCollectMaskedMainFamily(
                commands,
                collectionVolume,
                camera,
                collectMirrors,
                visibilityState,
                out int visibleRenderables))
        {
            CollectRenderedItems(commands, collectionVolume, camera, collectMirrors);
            return;
        }

        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        int emittedCommands = Math.Max(0, commands.GetUpdatingCommandCount() - commandsBefore);
        RuntimeEngine.Rendering.Stats.Octree.RecordOctreeCollect(visibleRenderables, emittedCommands);
        RuntimeEngine.Rendering.Stats.Octree.RecordCpuSpatialTreeStats(
            "BvhExactMaskedMainFamily",
            _bvhRenderTree.GetOccupancyStats(),
            elapsed);
    }
}
