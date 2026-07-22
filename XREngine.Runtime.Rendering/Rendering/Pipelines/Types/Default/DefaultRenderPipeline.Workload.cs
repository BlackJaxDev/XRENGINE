using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private static readonly int[] SceneMeshRenderPasses =
    [
        (int)EDefaultRenderPass.Background,
        (int)EDefaultRenderPass.OpaqueDeferred,
        (int)EDefaultRenderPass.DeferredDecals,
        (int)EDefaultRenderPass.OpaqueForward,
        (int)EDefaultRenderPass.MaskedForward,
        (int)EDefaultRenderPass.TransparentForward,
        (int)EDefaultRenderPass.WeightedBlendedOitForward,
        (int)EDefaultRenderPass.PerPixelLinkedListForward,
        (int)EDefaultRenderPass.DepthPeelingForward,
        (int)EDefaultRenderPass.OnTopForward,
    ];

    // The lightweight path explicitly preserves OpaqueForward and OnTopForward callbacks because
    // debug producers use those hooks to populate the late global debug batch. Other callback passes
    // retain the full pipeline until they can declare a more precise workload contract.
    private static readonly int[] FullPipelineCallbackRenderPasses =
    [
        (int)EDefaultRenderPass.Background,
        (int)EDefaultRenderPass.OpaqueDeferred,
        (int)EDefaultRenderPass.DeferredDecals,
        (int)EDefaultRenderPass.MaskedForward,
        (int)EDefaultRenderPass.TransparentForward,
        (int)EDefaultRenderPass.WeightedBlendedOitForward,
        (int)EDefaultRenderPass.PerPixelLinkedListForward,
        (int)EDefaultRenderPass.DepthPeelingForward,
    ];

    private static readonly int[] MotionVectorMeshRenderPasses =
    [
        (int)EDefaultRenderPass.OpaqueDeferred,
        (int)EDefaultRenderPass.DeferredDecals,
        (int)EDefaultRenderPass.OpaqueForward,
        (int)EDefaultRenderPass.MaskedForward,
        (int)EDefaultRenderPass.WeightedBlendedOitForward,
        (int)EDefaultRenderPass.PerPixelLinkedListForward,
        (int)EDefaultRenderPass.DepthPeelingForward,
    ];

    private bool ShouldRunFullScenePipeline()
    {
        if (RuntimeEngine.Rendering.State.IsShadowPass ||
            RuntimeEngine.Rendering.State.IsStereoPass ||
            RuntimeEngine.Rendering.State.IsSceneCapturePass ||
            RuntimeEngine.Rendering.State.IsLightProbePass ||
            RuntimeEngine.Rendering.State.IsMirrorPass ||
            RuntimeEnableMsaaTargets ||
            UsesVoxelConeTracing)
        {
            return true;
        }

        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null)
            return true;

        var commands = pipeline.ActiveMeshRenderCommands;

        if (commands.HasAnyRenderingMeshCommands(SceneMeshRenderPasses) ||
            commands.HasAnyRenderingCommands(FullPipelineCallbackRenderPasses))
        {
            return true;
        }

        if (ShouldRunAtmosphericScattering() ||
            ShouldRunVolumetricFog() ||
            HasFullPipelineDebugVisualization())
        {
            return true;
        }

        XRCamera? camera = pipeline.RenderState.SceneCamera ?? pipeline.LastSceneCamera;
        return camera?.ForwardPlusDebugMode != XRCamera.EForwardPlusDebugMode.None;
    }

    private static bool ShouldGenerateVelocityBufferForWorkload()
    {
        if (!ShouldGenerateVelocityBuffer())
            return false;

        var commands = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveMeshRenderCommands;
        return commands?.HasAnyRenderingMeshCommands(MotionVectorMeshRenderPasses) == true;
    }

    private bool HasFullPipelineDebugVisualization()
        => EnableTransformIdVisualization
        || EnableTransparencyAccumulationVisualization
        || EnableTransparencyRevealageVisualization
        || EnableTransparencyOverdrawVisualization
        || EnableFullOverdrawVisualization
        || EnablePerPixelLinkedListVisualization
        || EnableDepthPeelingLayerVisualization;
}
