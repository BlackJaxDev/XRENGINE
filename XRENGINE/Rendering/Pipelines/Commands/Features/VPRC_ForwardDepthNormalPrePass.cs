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

            using var overrideTicket = ActivePipelineInstance.RenderState.PushOverrideMaterial(material);
            using var variantTicket = ActivePipelineInstance.RenderState.PushUseDepthNormalMaterialVariants();
            using var pipelineTicket = ActivePipelineInstance.RenderState.PushForceShaderPipelines();
            using var generatedVertexTicket = ActivePipelineInstance.RenderState.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
                return;

            foreach (int pass in _renderPasses)
                if (_gpuDispatch)
                    commands.RenderGPU(pass);
                else
                    commands.RenderCPU(pass);
        }
    }
}
