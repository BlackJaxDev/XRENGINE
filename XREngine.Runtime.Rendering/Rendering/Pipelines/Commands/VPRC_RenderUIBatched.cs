using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.UI;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using System;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Pipeline command that renders UI elements using the <see cref="UIBatchCollector"/> for batched
/// instanced draws, then falls back to normal CPU rendering for any remaining non-batched commands.
/// <para>
/// This replaces <see cref="VPRC_RenderMeshesPass"/> in the <see cref="UserInterfaceRenderPipeline"/>
/// to achieve minimal draw call counts (typically 1 draw for all material quads + 1 draw for all text quads per pass).
/// </para>
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_RenderUIBatched : ViewportPopStateRenderCommand
{
    private int _renderPass;
    public int RenderPass
    {
        get => _renderPass;
        set => SetField(ref _renderPass, value);
    }

    public override string GpuProfilingName
        => $"{base.GpuProfilingName}[{GetRenderPassDisplayName(_renderPass)}]";

    protected override void Execute()
    {
        // Screen-space UI can be rendered while the parent pipeline has a
        // synthetic pass active.  Batched UI draws still need the UI pipeline's
        // pass index so Vulkan validates them against the UI pass metadata.
        using IDisposable passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_renderPass);

        XRCamera? camera = ActivePipelineInstance.RenderState.RenderingCamera
            ?? ActivePipelineInstance.RenderState.SceneCamera
            ?? ActivePipelineInstance.LastRenderingCamera
            ?? ActivePipelineInstance.LastSceneCamera;
        using IDisposable? cameraScope = camera is null
            ? null
            : ActivePipelineInstance.RenderState.PushRenderingCamera(camera);

        // Batched UI elements inject lightweight marker commands during collect-visible.
        // Rendering the CPU pass now executes inline batch groups and normal CPU fallback
        // commands in the same ordered stream.
        ActivePipelineInstance.MeshRenderCommands.RenderCPU(_renderPass, false);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        if (RenderPass < 0 && RenderPass != (int)EDefaultRenderPass.PreRender)
            return;

        string passName = $"RenderUIBatched_{RenderPass}";
        var builder = context.Metadata.ForPass(RenderPass, passName, ERenderGraphPassStage.Graphics);

        if (context.CurrentRenderTarget is { } target)
        {
            builder.WithName($"{passName}_{target.Name}");
            var colorLoad = target.ConsumeColorLoadOp();
            var depthLoad = target.ConsumeDepthLoadOp();
            var stencilLoad = target.ConsumeStencilLoadOp();

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                colorLoad,
                target.GetColorStoreOp());

            UseRenderTargetDepthStencilAttachments(builder, target, depthLoad, stencilLoad);
        }
    }

    private static string GetRenderPassDisplayName(int renderPass)
        => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
            ? ((EDefaultRenderPass)renderPass).ToString()
            : renderPass.ToString();
}
