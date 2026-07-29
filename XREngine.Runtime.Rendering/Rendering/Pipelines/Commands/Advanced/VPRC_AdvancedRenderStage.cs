using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Stable placeholder for one stage in the advanced frame contract.
/// Backends must not advertise the visibility-buffer shader family until every
/// production stage has a real implementation behind this command identity.
/// </summary>
public sealed class VPRC_AdvancedRenderStage : ViewportRenderCommand
{
    private EAdvancedRenderStage _stage;

    public EAdvancedRenderStage Stage
    {
        get => _stage;
        set => SetField(ref _stage, value);
    }

    public AdvancedRenderStageDescriptor Descriptor
        => AdvancedRenderPipelineFrameContract.GetDescriptor(Stage);

    public override string GpuProfilingName => Descriptor.GpuLabel;

    public override string CpuProfilingName => Descriptor.PassName;

    public VPRC_AdvancedRenderStage SetStage(EAdvancedRenderStage stage)
    {
        Stage = stage;
        return this;
    }

    protected override void Execute()
    {
        if (Stage != EAdvancedRenderStage.FrameBegin)
            return;

        XRRenderPipelineInstance.RenderingState state =
            ActivePipelineInstance.RenderState;
        if (state.WorldSnapshot is not RenderWorldSnapshot world)
            return;

        AdvancedSharedPreparationService.Instance.Acquire(
            world,
            state.FrameViewSet,
            EAdvancedPreparationConsumer.Visibility |
            EAdvancedPreparationConsumer.Depth |
            EAdvancedPreparationConsumer.Velocity |
            EAdvancedPreparationConsumer.MaterialReconstruction |
            EAdvancedPreparationConsumer.DirectionalShadow |
            EAdvancedPreparationConsumer.PointShadow |
            EAdvancedPreparationConsumer.SpotShadow |
            EAdvancedPreparationConsumer.Probe |
            EAdvancedPreparationConsumer.Capture);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        AdvancedRenderStageDescriptor descriptor = Descriptor;
        RenderPassBuilder builder = context.Metadata.ForPass(
            (int)descriptor.Stage,
            descriptor.PassName,
            descriptor.RenderGraphStage);

        int stageIndex = (int)descriptor.Stage;
        if (stageIndex > 0)
            builder.DependsOn(stageIndex - 1);
    }
}
