using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders a diagnostic direct-dispatch meshlet color overlay for renderers that
/// expose mesh shaders but cannot run the production zero-readback meshlet path.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_RenderMeshletDebugDisplay : ViewportRenderCommand
{
    private static readonly int[] DefaultRenderPasses =
    [
        (int)EDefaultRenderPass.OpaqueDeferred,
        (int)EDefaultRenderPass.OpaqueForward,
        (int)EDefaultRenderPass.MaskedForward,
        (int)EDefaultRenderPass.TransparentForward,
        (int)EDefaultRenderPass.WeightedBlendedOitForward,
        (int)EDefaultRenderPass.PerPixelLinkedListForward,
        (int)EDefaultRenderPass.DepthPeelingForward,
    ];

    public bool Enabled { get; set; } = false;
    public int[] RenderPasses { get; set; } = DefaultRenderPasses;

    protected override void Execute()
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
            return;

        XRRenderPipelineInstance activeInstance = ActivePipelineInstance;
        XRCamera? camera = activeInstance.RenderState.SceneCamera
            ?? activeInstance.RenderState.RenderingCamera
            ?? activeInstance.LastSceneCamera
            ?? activeInstance.LastRenderingCamera;

        bool enabled = Enabled || GpuBvhDebugSettings.IsMeshletDebugDisplayEnabled(camera);
        if (!enabled || camera is null)
            return;

        AbstractRenderer? renderer = AbstractRenderer.Current;
        bool supportsDiagnosticDirectDispatch = renderer?.SupportsDirectMeshTaskDispatch() == true;
        bool supportsProductionMeshletDispatch = renderer?.SupportsMeshletDispatch() == true;
        if (!supportsDiagnosticDirectDispatch)
        {
            if (supportsProductionMeshletDispatch)
                return;

            XREngine.Debug.RenderingWarningEvery(
                "MeshletDebugDisplay.DirectDispatchUnsupported",
                TimeSpan.FromSeconds(5),
                "[RenderDispatch] Meshlet debug display requires production meshlet dispatch or diagnostic direct mesh-task dispatch. Active renderer reason: {0}",
                renderer?.MeshletDispatchUnsupportedReason ?? "No active renderer.");
            return;
        }

        var scene = activeInstance.RenderState.Scene;
        if (scene is null)
            return;

        XRFrameBuffer.BoundForWriting?.RestoreDrawBuffers();
        RuntimeEngine.Rendering.State.ColorMask(true, true, true, true);
        RuntimeEngine.Rendering.State.EnableDepthTest(true);
        RuntimeEngine.Rendering.State.DepthFunc(EComparison.Always);
        RuntimeEngine.Rendering.State.AllowDepthWrite(false);
        RuntimeEngine.Rendering.State.EnableStencilTest(false);
        RuntimeEngine.Rendering.State.EnableBlend(false);

        using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
        using (activeInstance.RenderState.PushRenderingCamera(camera))
        {
            foreach (int renderPass in RenderPasses)
            {
                if (!activeInstance.ActiveMeshRenderCommands.HasRenderingCommands(renderPass))
                    continue;

                scene.GPUCommands.RenderMeshlets(
                    camera,
                    renderPass,
                    commandVisibility: null,
                    meshletDebugDisplay: true);
            }
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        context.GetOrCreateSyntheticPass(nameof(VPRC_RenderMeshletDebugDisplay), ERenderGraphPassStage.Graphics)
            .UseEngineDescriptors();
    }
}
