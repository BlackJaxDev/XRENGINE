using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugShapes : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
            {
                RuntimeEngine.Rendering.Debug.RenderShapes();
            }
        }
    }
}
