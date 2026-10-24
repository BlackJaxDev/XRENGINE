using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

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
        UIWorldCollectPacketSet? worldPacketSet = RetainFrozenWorldPacketSetForCollection();
        try
        {
        if (worldPacketSet?.ExpectedWorld is { } packetWorld)
            commands.ObserveWorldUiPacketSet(packetWorld, worldPacketSet);
        else
            commands.ObserveUnsupportedWorldUiTraversal();
        IRuntimeCullingCamera? cullingCamera = cullingCameraOverride?.Invoke() ?? camera;
        IVolume? collectionVolume = collectionVolumeOverride
            ?? (cullWithFrustum ? cullingCamera?.WorldFrustum() : null);
        bool allowGpuCollection = cullingCameraOverride is null && collectionVolumeOverride is null &&
            cullWithFrustum && CanCollectCoveredSourcesOnGpu(commands, visibilityState);
        int commandsBefore = commands.GetUpdatingCommandCount();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long maskedPathStart = CollectVisibleStageTimings.StartPath();
        bool maskedCollected = TryCollectMaskedMainFamily(
            commands,
            collectionVolume,
            camera,
            collectMirrors,
            visibilityState,
            allowGpuCollection,
            worldPacketSet,
            out int visibleRenderables);
        // A failed attempt records its time with no rows. The fallback path then opens its own path.
        CollectVisibleStageTimings.EndPath(
            ECollectVisiblePath.MaskedFamily,
            maskedPathStart,
            maskedCollected ? visibleRenderables : 0);
        if (!maskedCollected)
        {
            CollectRenderedItemsCore(commands, collectionVolume, camera, collectMirrors,
                allowGpuCollection, worldPacketSet);
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
        finally { worldPacketSet?.Release(); }
    }
}
