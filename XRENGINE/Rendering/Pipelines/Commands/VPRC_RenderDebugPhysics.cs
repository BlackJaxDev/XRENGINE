using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugPhysics : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass)
                return;

            using (Engine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
                ActivePipelineInstance.RenderState.WindowViewport?.World?.PhysicsScene?.DebugRender();
        }
    }
}
