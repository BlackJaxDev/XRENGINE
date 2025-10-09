namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Viewport render command that uses GPU-based indirect rendering.
    /// This command processes render commands using compute shaders for culling, sorting, and indirect rendering.
    /// </summary>
    public class VPRC_RenderMeshesPassGPU : ViewportRenderCommand
    {
        public VPRC_RenderMeshesPassGPU() { }
        public VPRC_RenderMeshesPassGPU(int renderPass)
            => RenderPass = renderPass;

        private int _renderPass = 0;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        protected override void Execute()
            => Pipeline.MeshRenderCommands.RenderGPU(RenderPass);
    }
} 