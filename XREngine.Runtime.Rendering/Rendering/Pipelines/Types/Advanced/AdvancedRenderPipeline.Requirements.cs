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
        bool multisample = requirements.OutputProfile.AntiAliasingMode == EAntiAliasingMode.Msaa &&
            requirements.OutputProfile.MsaaSampleCount > 1u;
        requirements.SupportedAntiAliasingModes.Add(EAntiAliasingMode.Msaa);
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
            requirements.RequireComputeProgram("advanced::aggregate-deformation");
            requirements.RequireComputeProgram("advanced::deformation-copy");
            requirements.RequireComputeProgram("advanced::compact-triangles");
            requirements.RequireComputeProgram("advanced::finalize-triangles");
        }
        if (IncludesStage(EAdvancedRenderStage.VisibilityRaster))
            requirements.RequireRasterProgram(multisample ? "advanced::visibility-pull-msaa" : "advanced::visibility-pull");
        if (IncludesStage(EAdvancedRenderStage.AmbientOcclusion))
            requirements.RequireComputeProgram("advanced::gtao");
        if (IncludesStage(EAdvancedRenderStage.DepthPyramidAndLateVisibility))
        {
            if (multisample) requirements.RequireRasterProgram("advanced::visibility-msaa-resolve");
            else requirements.RequireComputeProgram("advanced::depth-pyramid");
        }
        if (IncludesStage(EAdvancedRenderStage.WorkClassification))
        {
            requirements.RequireComputeProgram(multisample ? "advanced::shade-classify-msaa" : "advanced::shade-classify");
            requirements.RequireComputeProgram("advanced::shade-finalize");
        }
        if (IncludesStage(EAdvancedRenderStage.NativeOpaqueShading))
        {
            requirements.RequireRasterProgram("advanced::scene-copy");
            requirements.RequireComputeProgram(multisample ? "advanced::shade-native-msaa" : "advanced::shade-native");
            requirements.RequireComputeProgram(multisample ? "advanced::shade-msaa-resolve" : "advanced::shade-background");
            if (GlobalIlluminationPlan is { RequiresNativeMaterialSurfaceExports: true })
            {
                requirements.RequireComputeProgram(multisample ? "advanced::shade-surface-exports-msaa" : "advanced::shade-surface-exports");
                requirements.RequireComputeProgram(multisample ? "advanced::shade-background-exports-msaa" : "advanced::shade-background-exports");
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
