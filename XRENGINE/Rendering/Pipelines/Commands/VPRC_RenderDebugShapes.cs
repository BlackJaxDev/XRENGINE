using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderDebugShapes : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass)
                return;

            using (Engine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
                Engine.Rendering.Debug.RenderShapes();
        }
    }
}