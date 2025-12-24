namespace XREngine.Rendering.Pipelines.Commands
{
    /*
    public class VPRC_RenderMeshesPassCPU : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public VPRC_RenderMeshesPassCPU()
        {

        }
        public VPRC_RenderMeshesPassCPU(int renderPass)
        {
            RenderPass = renderPass;
        }

        public int RenderPass { get; set; } = 0;

        protected override void Execute()
        {
            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
                ActivePipelineInstance.RenderState.MeshRenderCommands?.RenderCPU(RenderPass);
        }
    }
    */
}
