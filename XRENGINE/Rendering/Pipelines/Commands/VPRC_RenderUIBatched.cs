using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.UI;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Pipeline command that renders UI elements using the <see cref="UIBatchCollector"/> for batched
/// instanced draws, then falls back to normal CPU rendering for any remaining non-batched commands.
/// <para>
/// This replaces <see cref="VPRC_RenderMeshesPass"/> in the <see cref="UserInterfaceRenderPipeline"/>
/// to achieve minimal draw call counts (typically 1 draw for all material quads + 1 draw for all text quads per pass).
/// </para>
/// </summary>
public class VPRC_RenderUIBatched : ViewportPopStateRenderCommand
{
    private int _renderPass;
    public int RenderPass
    {
        get => _renderPass;
        set => SetField(ref _renderPass, value);
    }

    protected override void Execute()
    {
        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(_renderPass);

        // Render batched UI elements (material quads + text quads)
        if (ActivePipelineInstance.Pipeline is UserInterfaceRenderPipeline uiPipeline)
        {
            uiPipeline.BatchCollector?.RenderMaterialQuadBatch(_renderPass);
            uiPipeline.BatchCollector?.RenderTextBatch(_renderPass);
        }

        // Render any remaining non-batched commands (e.g., clipped elements, custom materials)
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

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                colorLoad,
                target.GetColorStoreOp());

            builder.UseDepthAttachment(
                MakeFboDepthResource(target.Name),
                target.DepthAccess,
                depthLoad,
                target.GetDepthStoreOp());
        }
    }
}
