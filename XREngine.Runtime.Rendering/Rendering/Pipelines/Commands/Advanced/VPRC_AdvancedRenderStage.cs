using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Stable placeholder for one stage in the advanced frame contract.
/// Backends must not advertise the visibility-buffer shader family until every
/// production stage has a real implementation behind this command identity.
/// </summary>
public sealed class VPRC_AdvancedRenderStage : ViewportRenderCommand
{
    internal const string LateVisibilityRasterPassName =
        "Advanced.LateVisibilityRaster";

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
        XRRenderPipelineInstance.RenderingState state =
            ActivePipelineInstance.RenderState;
        if (state.WorldSnapshot is not RenderWorldSnapshot world)
            return;

        AdvancedPreparationPublication publication =
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

        if (Stage is not (EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster or
            EAdvancedRenderStage.DepthPyramidAndLateVisibility))
            return;

        AdvancedRenderPipelineOutputBinding binding =
            ActivePipelineInstance.AdvancedOutputBinding;
        AdvancedVisibilityFamilyReservation reservation = binding.Reservation;
        if (AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
            ActivePipelineInstance.Pipeline is not AdvancedRenderPipeline)
        {
            return;
        }

        // Output binding can be first evaluated while the native device and its
        // advanced resource generation are still coming online. Keep the
        // configured Advanced source intact and retry here, at the first
        // renderer-owned stage boundary, rather than permanently suppressing
        // every native stage after that transient admission failure.
        if (!binding.IsBound ||
            !renderer.IsAdvancedVisibilityFamilyReservationCurrent(in reservation))
        {
            XRViewport? viewport = state.WindowViewport
                ?? ActivePipelineInstance.LastWindowViewport;
            if (viewport is null)
                return;

            RuntimeEngine.Rendering.RefreshRenderPipelineOutputBinding(viewport);
            binding = ActivePipelineInstance.AdvancedOutputBinding;
            reservation = binding.Reservation;
            if (!binding.IsBound ||
                !renderer.IsAdvancedVisibilityFamilyReservationCurrent(in reservation))
            {
                return;
            }
        }

        if (!renderer.TryGetBackendCapability<IAdvancedVisibilityStageBackendCapability>(
                out IAdvancedVisibilityStageBackendCapability? visibility) ||
            visibility is null ||
            !visibility.SupportsAdvancedVisibilityStage(Stage))
        {
            return;
        }

        if (!ActivePipelineInstance.Resources.TryGetFrameBuffer(
                AdvancedVisibilityResourceNames.FrameBuffer,
                out XRFrameBuffer? target) ||
            target is null)
        {
            Debug.Out(
                $"Advanced visibility stage '{Stage}' has no realized '{AdvancedVisibilityResourceNames.FrameBuffer}' target in the active resource generation.");
            return;
        }

        AdvancedVisibilityStageBackendRequest request = new(
            Stage,
            EAdvancedVisibilityStageBackendPhase.Complete,
            reservation,
            publication,
            AdvancedSharedPreparationService.Instance.Extractor,
            world.FrameId,
            state.FrameViewSet ?? throw new InvalidOperationException(
                "Advanced visibility requires an immutable frame view set."),
            target,
            AdvancedVisibilityResourceNames.Identity,
            AdvancedVisibilityResourceNames.Metadata,
            AdvancedVisibilityResourceNames.Selection,
            AdvancedVisibilityResourceNames.DepthStencil,
            AdvancedVisibilityResourceNames.CurrentDepthPyramid);

        if (Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility)
        {
            EnqueueLatePhase(
                visibility,
                request with
                {
                    Phase = EAdvancedVisibilityStageBackendPhase.LateCompute,
                },
                Descriptor.PassName);
            EnqueueLatePhase(
                visibility,
                request with
                {
                    Phase = EAdvancedVisibilityStageBackendPhase.LateRaster,
                },
                LateVisibilityRasterPassName);
            return;
        }

        using IDisposable? passScope = PushRenderGraphPass(Descriptor.PassName);
        if (!visibility.TryEnqueueAdvancedVisibilityStage(
                in request,
                out string failureReason))
            ReportRejectedPhase(request.Phase, failureReason);
    }

    private void EnqueueLatePhase(
        IAdvancedVisibilityStageBackendCapability visibility,
        in AdvancedVisibilityStageBackendRequest request,
        string passName)
    {
        using IDisposable? passScope = PushRenderGraphPass(passName);
        if (!visibility.TryEnqueueAdvancedVisibilityStage(
                in request,
                out string failureReason))
            ReportRejectedPhase(request.Phase, failureReason);
    }

    private IDisposable? PushRenderGraphPass(string passName)
        => ParentPipeline?.TryGetRenderPassIndex(passName, out int passIndex) == true
            ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
            : null;

    private void ReportRejectedPhase(
        EAdvancedVisibilityStageBackendPhase phase,
        string failureReason)
        => Debug.Out(
            $"Advanced visibility stage '{Stage}' phase '{phase}' was rejected by the active backend: {failureReason}");

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
        if (descriptor.Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility)
        {
            builder.DependsOn(stageIndex - 1);
            DescribeLateRasterPass(context, stageIndex);
        }
        else if (descriptor.Stage == EAdvancedRenderStage.WorkClassification)
        {
            builder.DependsOn(context.GetOrCreateSyntheticPass(
                LateVisibilityRasterPassName,
                ERenderGraphPassStage.Graphics).PassIndex);
        }
        else if (stageIndex > 0)
            builder.DependsOn(stageIndex - 1);
    }

    private static void DescribeLateRasterPass(
        RenderGraphDescribeContext context,
        int computePassIndex)
    {
        RenderPassBuilder builder = context.GetOrCreateSyntheticPass(
                LateVisibilityRasterPassName,
                ERenderGraphPassStage.Graphics)
            .UseEngineDescriptors()
            .DependsOn(computePassIndex)
            .ReadBuffer(AdvancedVisibilityResourceNames.Payloads)
            .ReadBuffer(AdvancedVisibilityResourceNames.Producers)
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
        DescribeLateRasterSlotResources(builder);
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
                        AdvancedVisibilityResourceNames.PersistentState);
                DescribeLateComputeSlotResources(builder);
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

    private static void DescribeLateComputeSlotResources(
        RenderPassBuilder builder)
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

    private static void DescribeLateRasterSlotResources(
        RenderPassBuilder builder)
    {
        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            builder
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.LateArguments(slot),
                    ERenderPassResourceType.IndirectBuffer)
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.LateMeshTaskArguments(slot),
                    ERenderPassResourceType.IndirectBuffer)
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.LateMeshPayloads(slot))
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.LateVisiblePayloads(slot))
                .ReadBuffer(
                    AdvancedVisibilityResourceNames.RangeCounts(slot),
                    ERenderPassResourceType.IndirectBuffer);
        }
    }

    private static string Tex(string textureName)
        => RenderGraphResourceNames.MakeTexture(textureName);
}
