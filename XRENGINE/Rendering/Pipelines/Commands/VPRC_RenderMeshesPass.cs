namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderMeshesPass : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public int RenderPass { get; set; } = 0;

        protected override void Execute()
        {
            using (Pipeline.RenderState.PushRenderingCamera(Pipeline.RenderState.SceneCamera))
                Pipeline.RenderState.MeshRenderCommands?.Render(RenderPass);
        }
    }
}
