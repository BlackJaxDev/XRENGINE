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

        builder.UseEngineDescriptors();
        DescribeVisibilityResources(builder, descriptor.Stage);

        int stageIndex = (int)descriptor.Stage;
        if (stageIndex > 0)
            builder.DependsOn(stageIndex - 1);
    }

    private static void DescribeVisibilityResources(
        RenderPassBuilder builder,
        EAdvancedRenderStage stage)
    {
        switch (stage)
        {
            case EAdvancedRenderStage.VisibilityPreparation:
                builder
                    .ReadBuffer(AdvancedVisibilityResourceNames.Candidates)
                    .ReadBuffer(AdvancedVisibilityResourceNames.Payloads)
                    .ReadBuffer(AdvancedVisibilityResourceNames.Producers)
                    .ReadBuffer(
                        AdvancedVisibilityResourceNames.PayloadRangeIndices)
                    .ReadBuffer(
                        AdvancedVisibilityResourceNames.RangeArgumentOffsets)
                    .SampleTexture(Tex(
                        AdvancedVisibilityResourceNames.PreviousDepthPyramid))
                    .ReadWriteBuffer(
                        AdvancedVisibilityResourceNames.PersistentState);
                for (uint slot = 0u;
                     slot < AdvancedFrameSlotContract.DefaultSlotCount;
                     slot++)
                {
                    builder
                        .WriteBuffer(
                            AdvancedVisibilityResourceNames.EarlyArguments(slot),
                            ERenderPassResourceType.IndirectBuffer)
                        .WriteBuffer(
                            AdvancedVisibilityResourceNames.EarlyMeshTaskArguments(slot),
                            ERenderPassResourceType.IndirectBuffer)
                        .WriteBuffer(
                            AdvancedVisibilityResourceNames.EarlyMeshPayloads(slot))
                        .WriteBuffer(
                            AdvancedVisibilityResourceNames.DeferredCandidates(slot))
                        .WriteBuffer(
                            AdvancedVisibilityResourceNames.EarlyVisiblePayloads(slot))
                        .ReadWriteBuffer(
                            AdvancedVisibilityResourceNames.RangeCounts(slot))
                        .ReadWriteBuffer(
                            AdvancedVisibilityResourceNames.Counters(slot));
                }
                break;

            case EAdvancedRenderStage.VisibilityRaster:
                builder
                    .ReadBuffer(AdvancedVisibilityResourceNames.Payloads)
                    .ReadBuffer(AdvancedVisibilityResourceNames.Producers)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Identity),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Metadata),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Selection),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseDepthAttachment(
                        Tex(AdvancedVisibilityResourceNames.DepthStencil),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store);
                for (uint slot = 0u;
                     slot < AdvancedFrameSlotContract.DefaultSlotCount;
                     slot++)
                {
                    builder
                        .ReadBuffer(
                            AdvancedVisibilityResourceNames.EarlyArguments(slot),
                            ERenderPassResourceType.IndirectBuffer)
                        .ReadBuffer(
                            AdvancedVisibilityResourceNames.EarlyMeshTaskArguments(slot),
                            ERenderPassResourceType.IndirectBuffer)
                        .ReadBuffer(
                            AdvancedVisibilityResourceNames.EarlyMeshPayloads(slot))
                        .ReadBuffer(
                            AdvancedVisibilityResourceNames.EarlyVisiblePayloads(slot));
                }
                break;

            case EAdvancedRenderStage.DepthPyramidAndLateVisibility:
                builder
                    .SampleTexture(Tex(
                        AdvancedVisibilityResourceNames.DepthStencil))
                    .ReadWriteTexture(Tex(
                        AdvancedVisibilityResourceNames.CurrentDepthPyramid))
                    .ReadWriteBuffer(
                        AdvancedVisibilityResourceNames.PersistentState)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Identity),
                        ERenderGraphAccess.ReadWrite,
                        ERenderPassLoadOp.Load,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Metadata),
                        ERenderGraphAccess.ReadWrite,
                        ERenderPassLoadOp.Load,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        Tex(AdvancedVisibilityResourceNames.Selection),
                        ERenderGraphAccess.ReadWrite,
                        ERenderPassLoadOp.Load,
                        ERenderPassStoreOp.Store)
                    .UseDepthAttachment(
                        Tex(AdvancedVisibilityResourceNames.DepthStencil),
                        ERenderGraphAccess.ReadWrite,
                        ERenderPassLoadOp.Load,
                        ERenderPassStoreOp.Store);
                DescribeLateSlotResources(builder);
                break;

            case EAdvancedRenderStage.AttributeReconstruction:
                builder
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.Identity))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.Metadata))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.Selection))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.DepthStencil))
                    .ReadBuffer(AdvancedVisibilityResourceNames.Payloads)
                    .ReadBuffer(AdvancedVisibilityResourceNames.Producers);
                DescribeReconstructionSlotResources(builder);
                break;

        }
    }

    private static void DescribeReconstructionSlotResources(
        RenderPassBuilder builder)
    {
        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
            builder.ReadWriteBuffer(
                AdvancedReconstructionResourceNames.Counters(slot));
    }

    private static void DescribeLateSlotResources(RenderPassBuilder builder)
    {
        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            builder
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.DeferredCandidates(slot))
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.LateArguments(slot),
                    ERenderPassResourceType.IndirectBuffer)
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.LateMeshTaskArguments(slot),
                    ERenderPassResourceType.IndirectBuffer)
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.LateMeshPayloads(slot))
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.LateVisiblePayloads(slot))
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.RangeCounts(slot))
                .ReadWriteBuffer(
                    AdvancedVisibilityResourceNames.Counters(slot));
        }
    }

    private static string Tex(string textureName)
        => RenderGraphResourceNames.MakeTexture(textureName);
}
