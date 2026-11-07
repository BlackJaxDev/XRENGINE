using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

[RenderPipelineScriptCommand]
[RenderCommandInfo(ERenderCommandCategory.Lighting)]
public sealed class VPRC_SyncLightProbeResources : ViewportRenderCommand
{
    protected override void Execute()
    {
        if (ActivePipelineInstance?.Pipeline is IPbrLightingResourceProvider provider)
            provider.SyncPbrLightingResourcesForFrame();
    }
}
