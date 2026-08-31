using XREngine.Rendering.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Materializes the exact output-profiled resource generation required by an explicit production frame
    /// before visibility collection captures its package. This performs no command-chain execution or submission.
    /// </summary>
    public bool TryPrepareExplicitFrameResources(VisualScene scene, XRCamera camera, XRViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(viewport);
        if (!RuntimeEngine.IsRenderThread || AbstractRenderer.Current?.CurrentFrameOutput is null)
            return false;
        if (Pipeline is null || RuntimeRenderingHostServices.FrameTiming.IsPlayModeTransitioning)
            return false;

        ApplyCurrentFrameProfile(camera, stereoRightEyeCamera: null, viewport);
        if (FinalOutput is null)
            return false;

        using (RuntimeRenderingHostServices.Diagnostics.PushRenderingPipeline(this))
        using (RenderState.PushMainAttributes(
            viewport,
            scene,
            camera,
            stereoRightEyeCamera: null,
            target: null,
            shadowPass: false,
            stereoPass: false,
            globalMaterialOverride: null,
            screenSpaceUI: null,
            meshRenderCommands: MeshRenderCommands,
            applyRenderArea: false))
        {
            var dimensions = ResolvePipelineResourceDimensions(FinalOutput.Value);
            ResourceGenerationKey key = BuildResourceGenerationKey(
                dimensions.DisplayWidth,
                dimensions.DisplayHeight,
                dimensions.InternalWidth,
                dimensions.InternalHeight,
                viewport);
            if (ActiveGeneration?.Key != key && PendingGeneration?.Key != key &&
                !RequestResourceGeneration(key, "ExplicitPreCollect", force: true))
            {
                return false;
            }

            if (ActiveGeneration?.Key == key)
                return true;

            _ = TryPreparePendingGeneration(
                "ExplicitPreCollect",
                forceDue: true,
                catchUpMaxDuration: TimeSpan.Zero,
                catchUpMaxSpecsPerSlice: 0);
            return ActiveGeneration?.Key == key;
        }
    }
}
