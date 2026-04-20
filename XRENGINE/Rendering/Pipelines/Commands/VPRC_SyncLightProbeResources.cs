using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

[RenderPipelineScriptCommand]
public sealed class VPRC_SyncLightProbeResources : ViewportRenderCommand
{
    protected override void Execute()
    {
        switch (ActivePipelineInstance?.Pipeline)
        {
            case DefaultRenderPipeline pipeline:
                pipeline.SyncPbrLightingResourcesForFrame();
                break;
            case DefaultRenderPipeline2 pipeline:
                pipeline.SyncPbrLightingResourcesForFrame();
                break;
        }
    }
}