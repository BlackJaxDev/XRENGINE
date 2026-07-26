using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders a nested sub-chain with a scoped wireframe rasterization mode.
/// Current implementation is backed by OpenGL polygon mode because the shared renderer
/// abstraction does not yet expose generic fill-mode switching.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DebugWireframe : ViewportRenderCommand
{
    private ViewportRenderCommandContainer? _body;
    public bool ExecuteBodyWhenUnsupported { get; set; } = true;

    public ViewportRenderCommandContainer? Body
    {
        get => _body;
        set
        {
            _body = value;
            AttachPipeline(_body);
        }
    }

    public override bool NeedsCollecVisible => Body is not null;

    protected override void Execute()
    {
        if (Body is null)
            return;

        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        if (renderer is null ||
            !renderer.TryGetBackendCapability<IRasterizationModeBackendCapability>(out var rasterization) ||
            rasterization is null)
        {
            if (ExecuteBodyWhenUnsupported)
            {
                using var fallbackBranchScope = ActivePipelineInstance.PushRenderGraphBranchScope();
                Body.Execute();
            }

            return;
        }

        using var branchScope = ActivePipelineInstance.PushRenderGraphBranchScope();
        rasterization.SetWireframeRasterization(true);
        try
        {
            Body.Execute();
        }
        finally
        {
            rasterization.SetWireframeRasterization(false);
        }
    }

    public override void CollectVisible()
        => Body?.CollectVisible();

    public override void SwapBuffers()
        => Body?.SwapBuffers();

    internal override void OnAttachedToContainer()
    {
        base.OnAttachedToContainer();
        AttachPipeline(_body);
    }

    internal override void OnParentPipelineAssigned()
    {
        base.OnParentPipelineAssigned();
        AttachPipeline(_body);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        Body?.BuildRenderPassMetadata(context);
    }

    private void AttachPipeline(ViewportRenderCommandContainer? container)
    {
        var pipeline = CommandContainer?.ParentPipeline;
        if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
            container.ParentPipeline = pipeline;
    }
}
