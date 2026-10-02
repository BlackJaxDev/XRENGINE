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
    }
}
