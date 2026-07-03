using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Declares one Retinal Visibility Cache frame-graph stage and emits a profiling marker.
/// The concrete shader kernels bind to these resources through the backend render graph.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_RvcPass : ViewportRenderCommand
{
    private ERvcGpuPassStage _stage = ERvcGpuPassStage.VisibilityTargets;
    private string _stageLabel = nameof(ERvcGpuPassStage.VisibilityTargets);

    public ERvcGpuPassStage Stage
    {
        get => _stage;
        set
        {
            if (SetField(ref _stage, value))
                _stageLabel = value.ToString();
        }
    }

    public override string GpuProfilingName => GetGpuProfilingNameWithSuffix(_stageLabel);

    protected override bool ShouldExecuteThisFrame()
    {
        if (ParentPipeline is not RvcRenderPipeline pipeline)
            return false;

        RvcPipelinePlan plan = pipeline.LastRvcPlan;
        return plan.Resolution.IsRvcActive &&
            (plan.GpuPassExecution.PlannedStages & Stage) != 0 &&
            (plan.GpuPassExecution.BackendImplementedStages & Stage) != 0;
    }

    protected override void Execute()
    {
        Debug.RenderingWarningEvery(
            $"RVC.Pass.{Stage}.KernelPending",
            TimeSpan.FromSeconds(5),
            "[RVC] Stage {0} is active in the frame graph; backend shader dispatch is not linked yet.",
            Stage);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        RenderPassBuilder builder = context
            .GetOrCreateSyntheticPass(BuildPassName(Stage), ResolveRenderGraphStage(Stage))
            .WithName($"RVC {Stage}")
            .UseEngineDescriptors();

        if (UsesMaterialDescriptors(Stage))
            builder.UseMaterialDescriptors();

        AddStageDependencies(context, builder, Stage);
        DescribeStageResources(builder, Stage);
    }

    private static string BuildPassName(ERvcGpuPassStage stage)
        => $"RVC.{stage}";

    private static ERenderGraphPassStage ResolveRenderGraphStage(ERvcGpuPassStage stage)
        => stage switch
        {
            ERvcGpuPassStage.OpenXrVisibilityMaskStencil or
            ERvcGpuPassStage.VisibilityTargets or
            ERvcGpuPassStage.FoveatedShadingRate or
            ERvcGpuPassStage.TransparencyForwardPlus or
            ERvcGpuPassStage.FoveatedResolve or
            ERvcGpuPassStage.DiagnosticOverlay => ERenderGraphPassStage.Graphics,
            _ => ERenderGraphPassStage.Compute,
        };

    private static bool UsesMaterialDescriptors(ERvcGpuPassStage stage)
        => stage is
            ERvcGpuPassStage.VisibilityTargets or
            ERvcGpuPassStage.AttributeReconstruction or
            ERvcGpuPassStage.PixelToShadeletMap or
            ERvcGpuPassStage.MaterialShadeletShading or
            ERvcGpuPassStage.SharedLighting or
            ERvcGpuPassStage.TransparencyForwardPlus or
            ERvcGpuPassStage.FoveatedResolve;

    private static void AddStageDependencies(
        RenderGraphDescribeContext context,
        RenderPassBuilder builder,
        ERvcGpuPassStage stage)
    {
        ERvcGpuPassStage previous = stage switch
        {
            ERvcGpuPassStage.VisibilityTargets => ERvcGpuPassStage.OpenXrVisibilityMaskStencil,
            ERvcGpuPassStage.AttributeReconstruction => ERvcGpuPassStage.VisibilityTargets,
            ERvcGpuPassStage.HzbRejection => ERvcGpuPassStage.AttributeReconstruction,
            ERvcGpuPassStage.PixelToShadeletMap => ERvcGpuPassStage.HzbRejection,
            ERvcGpuPassStage.MaterialShadeletShading => ERvcGpuPassStage.PixelToShadeletMap,
            ERvcGpuPassStage.FoveatedShadingRate => ERvcGpuPassStage.PixelToShadeletMap,
            ERvcGpuPassStage.HeadSpaceLightClusters => ERvcGpuPassStage.MaterialShadeletShading,
            ERvcGpuPassStage.SharedLighting => ERvcGpuPassStage.HeadSpaceLightClusters,
            ERvcGpuPassStage.ReuseValidation => ERvcGpuPassStage.SharedLighting,
            ERvcGpuPassStage.TemporalCache => ERvcGpuPassStage.ReuseValidation,
            ERvcGpuPassStage.TransparencyForwardPlus => ERvcGpuPassStage.TemporalCache,
            ERvcGpuPassStage.FoveatedResolve => ERvcGpuPassStage.TransparencyForwardPlus,
            ERvcGpuPassStage.DiagnosticOverlay => ERvcGpuPassStage.FoveatedResolve,
            _ => ERvcGpuPassStage.None,
        };

        if (previous != ERvcGpuPassStage.None)
            builder.DependsOn(context.GetOrCreateSyntheticPass(BuildPassName(previous), ResolveRenderGraphStage(previous)).PassIndex);
    }

    private static void DescribeStageResources(RenderPassBuilder builder, ERvcGpuPassStage stage)
    {
        switch (stage)
        {
            case ERvcGpuPassStage.OpenXrVisibilityMaskStencil:
                builder
                    .ReadBuffer(RvcFrameGraphContract.SharedOpenXrVisibilityMaskVertices, ERenderPassResourceType.VertexBuffer)
                    .ReadBuffer(RvcFrameGraphContract.SharedOpenXrVisibilityMaskIndices, ERenderPassResourceType.IndexBuffer)
                    .UseDepthAttachment(Tex(RvcFrameGraphContract.PerViewDepthArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store)
                    .UseStencilAttachment(Tex(RvcFrameGraphContract.PerViewDepthArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store);
                break;
            case ERvcGpuPassStage.VisibilityTargets:
                builder
                    .UseColorAttachment(Tex(RvcFrameGraphContract.PerViewVisibilityArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store)
                    .UseColorAttachment(Tex(RvcFrameGraphContract.PerViewVelocityArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store)
                    .UseDepthAttachment(Tex(RvcFrameGraphContract.PerViewDepthArray), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedVisibilitySourceRecords)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedIndirectArguments, ERenderPassResourceType.IndirectBuffer)
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialResourceRows);
                break;
            case ERvcGpuPassStage.AttributeReconstruction:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewVisibilityArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewDepthArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewVelocityArray))
                    .ReadBuffer(RvcFrameGraphContract.SharedVisibilitySourceRecords)
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialResourceRows)
                    .ReadWriteTexture(Tex(RvcFrameGraphContract.PerViewReconstructionErrorArray));
                break;
            case ERvcGpuPassStage.HzbRejection:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewDepthArray))
                    .ReadWriteTexture(Tex(RvcFrameGraphContract.PerViewHzbDepthArray))
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedVisibilitySourceRecords);
                break;
            case ERvcGpuPassStage.PixelToShadeletMap:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewVisibilityArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewHzbDepthArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewReconstructionErrorArray))
                    .ReadWriteTexture(Tex(RvcFrameGraphContract.PerViewPixelToShadeletArray))
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedMaterialShadelets)
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialResourceRows);
                break;
            case ERvcGpuPassStage.MaterialShadeletShading:
                builder
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialShadelets)
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialResourceRows)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedLighting);
                break;
            case ERvcGpuPassStage.FoveatedShadingRate:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewPixelToShadeletArray))
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialShadelets);
                break;
            case ERvcGpuPassStage.HeadSpaceLightClusters:
                builder
                    .ReadBuffer("ForwardPlusLocalLights")
                    .ReadBuffer("ForwardPlusVisibleIndices")
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedHeadSpaceLightClusters);
                break;
            case ERvcGpuPassStage.SharedLighting:
                builder
                    .ReadBuffer(RvcFrameGraphContract.SharedHeadSpaceLightClusters)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedLightReservoirs)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedLighting);
                break;
            case ERvcGpuPassStage.ReuseValidation:
                builder
                    .ReadBuffer(RvcFrameGraphContract.SharedMaterialShadelets)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedLighting)
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewReconstructionErrorArray));
                break;
            case ERvcGpuPassStage.TemporalCache:
                builder
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedTemporalCache)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedLighting)
                    .ReadWriteBuffer(RvcFrameGraphContract.SharedCounters);
                break;
            case ERvcGpuPassStage.TransparencyForwardPlus:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewDepthArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewPixelToShadeletArray))
                    .UseColorAttachment(Tex(RvcFrameGraphContract.TransparencyTargetArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store)
                    .ReadBuffer(RvcFrameGraphContract.SharedLighting);
                break;
            case ERvcGpuPassStage.FoveatedResolve:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.TransparencyTargetArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewPixelToShadeletArray))
                    .ReadBuffer(RvcFrameGraphContract.SharedLighting)
                    .UseColorAttachment(Tex(RvcFrameGraphContract.FinalResolveArray), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store);
                break;
            case ERvcGpuPassStage.DiagnosticOverlay:
                builder
                    .SampleTexture(Tex(RvcFrameGraphContract.FinalResolveArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewVisibilityArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewHzbDepthArray))
                    .SampleTexture(Tex(RvcFrameGraphContract.PerViewPixelToShadeletArray))
                    .UseColorAttachment(Tex(RvcFrameGraphContract.MirrorDebug), ERenderGraphAccess.Write, ERenderPassLoadOp.Clear, ERenderPassStoreOp.Store)
                    .ReadBuffer(RvcFrameGraphContract.SharedCounters);
                break;
        }
    }

    private static string Tex(string textureName)
        => RenderGraphResourceNames.MakeTexture(textureName);
}
