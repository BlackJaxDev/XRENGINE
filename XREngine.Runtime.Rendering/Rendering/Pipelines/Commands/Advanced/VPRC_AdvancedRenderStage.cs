using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Dispatches the native visibility and opaque compute stages through the backend's
/// immutable frame family. Late/post commands follow their contract markers.
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
        {
            string snapshotType = state.WorldSnapshot?.GetType().FullName ?? "null";
            ReportExecutionPrerequisiteRejection($"Render world snapshot is unavailable (actual: {snapshotType}).");
            return;
        }

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
            EAdvancedRenderStage.DepthPyramidAndLateVisibility or
            EAdvancedRenderStage.AmbientOcclusion or
            EAdvancedRenderStage.WorkClassification or
            EAdvancedRenderStage.NativeOpaqueShading))
        {
            using IDisposable? stagePassScope = PushRenderGraphPass(Descriptor.PassName);
            return;
        }

        AdvancedRenderPipelineOutputBinding binding =
            ActivePipelineInstance.AdvancedOutputBinding;
        AdvancedVisibilityFamilyReservation reservation = binding.Reservation;
        if (AbstractRenderer.Current is not IRuntimeRendererHost renderer)
        {
            ReportExecutionPrerequisiteRejection("The active renderer does not expose the runtime renderer host contract.");
            return;
        }
        if (ActivePipelineInstance.Pipeline is not AdvancedRenderPipeline pipeline)
        {
            string pipelineType = ActivePipelineInstance.Pipeline?.GetType().FullName ?? "null";
            ReportExecutionPrerequisiteRejection($"The active pipeline is not AdvancedRenderPipeline (actual: {pipelineType}).");
            return;
        }
        IAdvancedAmbientOcclusionProvider? ambientOcclusionProvider = pipeline.AmbientOcclusionProvider;
        if (ambientOcclusionProvider is not null && ambientOcclusionProvider is not AdvancedDepthGtaoProvider)
        {
            ReportAdmissionRejection($"Ambient occlusion provider '{ambientOcclusionProvider.ProviderName}' has no native Advanced compute implementation.");
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
            {
                ReportAdmissionRejection("No viewport is available to refresh the Advanced output binding.");
                return;
            }

            RuntimeEngine.Rendering.RefreshRenderPipelineOutputBinding(viewport);
            binding = ActivePipelineInstance.AdvancedOutputBinding;
            reservation = binding.Reservation;
            if (!binding.IsBound ||
                !renderer.IsAdvancedVisibilityFamilyReservationCurrent(in reservation))
            {
                string reason = binding.FailureReason
                    ?? "The Advanced output binding remained unbound or stale after refresh.";
                ReportAdmissionRejection(reason);
                return;
            }
        }

        if (!renderer.TryGetBackendCapability<IAdvancedVisibilityStageBackendCapability>(
                out IAdvancedVisibilityStageBackendCapability? visibility) ||
            visibility is null ||
            !visibility.SupportsAdvancedVisibilityStage(Stage))
        {
            ReportStageCapabilityRejection();
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
            AdvancedAmbientOcclusionContract.ResourceName,
            AdvancedVisibilityResourceNames.CurrentDepthPyramid,
            pipeline.ShadingDebugView,
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode == EAdvancedRenderPipelineMode.Required,
            pipeline.EnableBuiltInAmbientOcclusion &&
            ambientOcclusionProvider is AdvancedDepthGtaoProvider { IsSupported: true });

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

    private void ReportExecutionPrerequisiteRejection(string reason)
        => Debug.RenderingWarningEvery(
            $"AdvancedVisibility.Prerequisite.{Stage}.{reason}",
            TimeSpan.FromSeconds(30),
            "[AdvancedPipeline] Stage '{0}' cannot execute: {1}",
            Stage,
            reason);
    private void ReportAdmissionRejection(string reason)
        => Debug.RenderingWarningEvery(
            $"AdvancedVisibility.Admission.{Stage}.{reason}",
            TimeSpan.FromSeconds(30),
            "[AdvancedPipeline] Stage '{0}' is waiting for a current Advanced output binding: {1}",
            Stage,
            reason);

    private void ReportStageCapabilityRejection()
        => Debug.RenderingWarningEvery(
            $"AdvancedVisibility.StageCapability.{Stage}",
            TimeSpan.FromSeconds(30),
            "[AdvancedPipeline] Stage '{0}' is not supported by the active Advanced visibility backend.",
            Stage);

    private void ReportRejectedPhase(        EAdvancedVisibilityStageBackendPhase phase,
        string failureReason)
        => Debug.Out(
            $"Advanced visibility stage '{Stage}' phase '{phase}' was rejected by the active backend: {failureReason}");

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        AdvancedRenderStageDescriptor descriptor = Descriptor;
        // Stage ordinals describe the Advanced command chain, while the mesh
        // passes use the same small integers for their own collection keys.
        // Give graph nodes their own identities so these unrelated passes
        // cannot merge their resource accesses or dependencies.
        RenderPassBuilder builder = context.GetOrCreateSyntheticPass(
            descriptor.PassName,
            descriptor.RenderGraphStage);

        builder.UseEngineDescriptors();
        DescribeVisibilityResources(builder, descriptor.Stage);

        int stageIndex = (int)descriptor.Stage;
        if (descriptor.Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility)
        {
            builder.DependsOn(GetPreviousStagePassIndex(context, stageIndex));
            DescribeLateRasterPass(context, builder.PassIndex);
        }
        else if (descriptor.Stage == EAdvancedRenderStage.AmbientOcclusion)
        {
            builder.DependsOn(context.GetOrCreateSyntheticPass(
                LateVisibilityRasterPassName,
                ERenderGraphPassStage.Graphics).PassIndex);
        }
        else if (descriptor.Stage == EAdvancedRenderStage.WorkClassification)
            builder.DependsOn(GetPreviousStagePassIndex(context, stageIndex));
        else if (stageIndex > 0)
            builder.DependsOn(GetPreviousStagePassIndex(context, stageIndex));
    }

    private static int GetPreviousStagePassIndex(
        RenderGraphDescribeContext context,
        int stageIndex)
    {
        AdvancedRenderStageDescriptor previous =
            AdvancedRenderPipelineFrameContract.GetDescriptor(
                (EAdvancedRenderStage)(stageIndex - 1));
        return context.GetOrCreateSyntheticPass(
            previous.PassName,
            previous.RenderGraphStage).PassIndex;
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
                RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 0),
                ERenderGraphAccess.ReadWrite,
                ERenderPassLoadOp.Load,
                ERenderPassStoreOp.Store)
            .UseColorAttachment(
                RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 1),
                ERenderGraphAccess.ReadWrite,
                ERenderPassLoadOp.Load,
                ERenderPassStoreOp.Store)
            .UseColorAttachment(
                RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 2),
                ERenderGraphAccess.ReadWrite,
                ERenderPassLoadOp.Load,
                ERenderPassStoreOp.Store)
            .UseDepthAttachment(
                RenderGraphResourceNames.MakeFboDepth(AdvancedVisibilityResourceNames.FrameBuffer),
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
                        RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 0),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 1),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseColorAttachment(
                        RenderGraphResourceNames.MakeFboColor(AdvancedVisibilityResourceNames.FrameBuffer, 2),
                        ERenderGraphAccess.Write,
                        ERenderPassLoadOp.Clear,
                        ERenderPassStoreOp.Store)
                    .UseDepthAttachment(
                        RenderGraphResourceNames.MakeFboDepth(AdvancedVisibilityResourceNames.FrameBuffer),
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

            case EAdvancedRenderStage.WorkClassification:
                builder.SampleTexture(Tex(AdvancedVisibilityResourceNames.Identity))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.Metadata));
                for (uint slot = 0; slot < AdvancedFrameSlotContract.DefaultSlotCount; ++slot)
                    builder.WriteBuffer(AdvancedClassificationResourceNames.ActiveTiles(slot))
                        .WriteBuffer(AdvancedClassificationResourceNames.KernelTiles(slot))
                        .ReadWriteBuffer(AdvancedClassificationResourceNames.Counters(slot))
                        .ReadWriteBuffer(AdvancedClassificationResourceNames.KernelCounts(slot))
                        .WriteBuffer(AdvancedClassificationResourceNames.DispatchArgs(slot), ERenderPassResourceType.IndirectBuffer);
                break;

            case EAdvancedRenderStage.AmbientOcclusion:
                builder.SampleTexture(Tex(AdvancedVisibilityResourceNames.DepthStencil))
                    .ReadWriteTexture(Tex(AdvancedAmbientOcclusionContract.ResourceName));
                break;

            case EAdvancedRenderStage.NativeOpaqueShading:
                // ShadeNativeOpaque reconstructs the local surface on demand;
                // there is no intermediate AttributeReconstruction pass.
                builder.SampleTexture(Tex(AdvancedVisibilityResourceNames.Identity))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.Metadata))
                    .SampleTexture(Tex(AdvancedVisibilityResourceNames.DepthStencil))
                    .SampleTexture(Tex(AdvancedAmbientOcclusionContract.ResourceName))
                    .ReadWriteTexture(Tex(AdvancedRenderPipeline.HDRSceneTextureName))
                    .ReadWriteTexture(Tex(AdvancedRenderPipeline.VelocityTextureName))
                    .ReadWriteTexture(Tex(AdvancedTemporalHistoryContract.ReactiveMaskResourceName))
                    .ReadWriteTexture(Tex(AdvancedShadingResourceNames.ShadingDiagnostics));
                for (uint slot = 0; slot < AdvancedFrameSlotContract.DefaultSlotCount; ++slot)
                    builder.ReadBuffer(AdvancedClassificationResourceNames.ActiveTiles(slot))
                        .ReadBuffer(AdvancedClassificationResourceNames.KernelTiles(slot))
                        .ReadBuffer(AdvancedClassificationResourceNames.Counters(slot))
                        .ReadBuffer(AdvancedClassificationResourceNames.KernelCounts(slot))
                        .ReadBuffer(AdvancedClassificationResourceNames.DispatchArgs(slot), ERenderPassResourceType.IndirectBuffer)
                        .ReadWriteBuffer(AdvancedClusteredLightingResourceNames.FroxelGrid(slot))
                        .ReadWriteBuffer(AdvancedClusteredLightingResourceNames.LightIndexList(slot))
                        .ReadWriteBuffer(AdvancedClusteredLightingResourceNames.LightingCounters(slot));
                break;

        }
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
