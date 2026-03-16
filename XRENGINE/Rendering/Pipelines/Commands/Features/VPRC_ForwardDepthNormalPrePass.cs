using System.Collections.Generic;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders forward opaque and masked geometry into the shared depth+normal targets.
    /// Uses per-material fragment variants when available so the pre-pass preserves each shader's
    /// own normal evaluation path, with a generic override material left as fallback.
    /// </summary>
    public class VPRC_ForwardDepthNormalPrePass : ViewportRenderCommand
    {
        private IReadOnlyList<int> _renderPasses = [];
        private bool _gpuDispatch;

        public void SetOptions(IReadOnlyList<int> renderPasses, bool gpuDispatch)
        {
            _renderPasses = renderPasses;
            _gpuDispatch = gpuDispatch;
        }

        protected override void Execute()
        {
            if (ParentPipeline is not DefaultRenderPipeline pipeline || _renderPasses.Count == 0)
                return;
            
            var material = pipeline.GetDepthNormalPrePassMaterial();
            var rs = ActivePipelineInstance.RenderState;

            using var overrideTicket = rs.PushOverrideMaterial(material);
            using var variantTicket = rs.PushUseDepthNormalMaterialVariants();
            using var pipelineTicket = rs.PushForceShaderPipelines();
            using var generatedVertexTicket = rs.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
                return;

            var camera = rs.SceneCamera;
            foreach (int pass in _renderPasses)
            {
                // This pre-pass relies on override materials, generated vertex programs,
                // and per-material depth-normal variants. Those are honored by the CPU
                // draw path; the GPU-indirect path does not reliably preserve this state.
                commands.RenderCPU(pass, false, camera);
            }
        }
    }
}
