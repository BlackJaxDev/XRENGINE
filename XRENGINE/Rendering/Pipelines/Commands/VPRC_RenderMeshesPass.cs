namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Backward-compatible mesh rendering command alias.
    /// Shared path routing now lives in <see cref="VPRC_RenderMeshesPassShared"/>.
    /// </summary>
    public class VPRC_RenderMeshesPass : VPRC_RenderMeshesPassShared
    {
        public VPRC_RenderMeshesPass()
        {
        }

        public VPRC_RenderMeshesPass(int renderPass, bool gpuDispatch)
            : base(renderPass, gpuDispatch)
        {
        }
    }
} 
