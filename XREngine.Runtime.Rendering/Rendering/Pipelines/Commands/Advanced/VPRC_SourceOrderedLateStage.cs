using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Holds one published command package while the source pass draws its prefix,
/// copies HDR color, and draws its suffix.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_SourceOrderedLateStage : ViewportRenderCommand
{
    private ViewportRenderCommandContainer? _commands;

    public ViewportRenderCommandContainer? Commands
    {
        get => _commands;
        set
        {
            if (!SetField(ref _commands, value))
                return;
            AttachPipeline();
            ParentPipeline?.NotifyCommandChainStructureChanged();
        }
    }

    public override void DescribeRequirements(RenderPipelineRequirements requirements)
        => requirements.Include(Commands);

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        Commands?.BuildRenderPassMetadata(context);
        int prefix = context.GetOrCreateSyntheticPass(
            "Advanced.SourceOrderedPrefix").PassIndex;
        string copyName = VPRC_RenderQuadToFBO.BuildQuadBlitPassName(
            AdvancedRenderPipeline.SceneCopyFBOName,
            AdvancedRenderPipeline.SourceOrderedSceneCopyFBOName);
        var copyPass = context.GetOrCreateSyntheticPass(copyName);
        int copy = copyPass.PassIndex;
        copyPass.DependsOn(prefix);
        context.GetOrCreateSyntheticPass("Advanced.SourceOrderedSuffix")
            .DependsOn(copy);
    }

    internal override void OnAttachedToContainer()
    {
        base.OnAttachedToContainer();
        AttachPipeline();
    }

    internal override void OnParentPipelineAssigned()
    {
        base.OnParentPipelineAssigned();
        AttachPipeline();
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance? instance = ActivePipelineInstance;
        if (instance is null)
            return;

        RenderCommandCollection commandCollection = instance.ActiveMeshRenderCommands;
        using var scope = commandCollection.EnterRenderingBufferReadScope();
        if (commandCollection.RenderingBackendReadyPackage.State !=
            EBackendReadyFramePackageState.Published)
        {
            instance.RenderState.RequestPendingResourcePreparationDeferral(
                "The source-ordered late pass has no published command package.");
            return;
        }

        using var required = RequiredMeshSubmissionScope.EnterIf(true);
        Commands?.Execute();
    }

    private void AttachPipeline()
    {
        RenderPipeline? pipeline = ParentPipeline;
        if (Commands is not null && pipeline is not null &&
            !ReferenceEquals(Commands.ParentPipeline, pipeline))
            Commands.ParentPipeline = pipeline;
    }
}
