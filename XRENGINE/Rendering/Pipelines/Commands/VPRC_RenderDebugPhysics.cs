namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderDebugPhysics : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass)
                return;

            using (Pipeline.RenderState.PushRenderingCamera(Pipeline.RenderState.SceneCamera))
                Pipeline.RenderState.WindowViewport?.World?.PhysicsScene?.DebugRender();
        }
    }
}