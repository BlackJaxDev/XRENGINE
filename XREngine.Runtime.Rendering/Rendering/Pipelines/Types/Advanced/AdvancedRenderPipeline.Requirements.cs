using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>Exposes native visibility prerequisites independently of the pipeline asset type.</summary>
    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        if (requirements.Backend != RendererBackendId.WebGPU)
        {
            base.DescribeRequirements(requirements);
            return;
        }
        requirements.RequireOperation("integer-color-targets");
        requirements.RequireOperation("storage-images");
        requirements.RequireOperation("gpu-driven-meshes");
        requirements.RequireOperation("memory-barriers");
        requirements.RequireOperation("advanced-stage-execution");
        requirements.ScenePasses.Add((int)EDefaultRenderPass.Background);
        requirements.ScenePasses.Add((int)EDefaultRenderPass.OpaqueDeferred);
        requirements.ScenePasses.Add((int)EDefaultRenderPass.OpaqueForward);
        requirements.ScenePasses.Add((int)EDefaultRenderPass.MaskedForward);
        if (IncludesStage(EAdvancedRenderStage.VisibilityPreparation))
        {
            requirements.RequireComputeProgram("advanced::compact-triangles");
            requirements.RequireComputeProgram("advanced::finalize-triangles");
        }
        if (IncludesStage(EAdvancedRenderStage.VisibilityRaster))
            requirements.RequireRasterProgram("advanced::visibility-pull");
        if (IncludesStage(EAdvancedRenderStage.AmbientOcclusion))
            requirements.RequireComputeProgram("advanced::gtao");
        if (IncludesStage(EAdvancedRenderStage.DepthPyramidAndLateVisibility))
            requirements.RequireComputeProgram("advanced::depth-pyramid");
        if (IncludesStage(EAdvancedRenderStage.WorkClassification))
        {
            requirements.RequireComputeProgram("advanced::shade-classify");
            requirements.RequireComputeProgram("advanced::shade-finalize");
        }
        if (IncludesStage(EAdvancedRenderStage.NativeOpaqueShading))
        {
            requirements.RequireRasterProgram("advanced::scene-copy");
            requirements.RequireComputeProgram("advanced::shade-native");
            requirements.RequireComputeProgram("advanced::shade-background");
            if (GlobalIlluminationPlan is { RequiresNativeMaterialSurfaceExports: true })
            {
                requirements.RequireComputeProgram("advanced::shade-surface-exports");
                requirements.RequireComputeProgram("advanced::shade-background-exports");
            }
        }
        if (IncludesStage(EAdvancedRenderStage.TemporalAndPostProcessing) && AllowsPostProcessing)
        {
            requirements.RequireRasterProgram("advanced::post-process");
            requirements.RequireRasterProgram("advanced::final-post-process");
        }
        if (IncludesStage(EAdvancedRenderStage.Output))
            requirements.RequireRasterProgram("advanced::present");
        DescribeAdvancedWebPostRequirements(requirements);
    }
}
