using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderMotionVectorsPass : ViewportRenderCommand
    {
        private static readonly int[] DefaultRenderPasses =
        [
            (int)EDefaultRenderPass.Background,
            (int)EDefaultRenderPass.OpaqueDeferred,
            (int)EDefaultRenderPass.DeferredDecals,
            (int)EDefaultRenderPass.OpaqueForward,
            (int)EDefaultRenderPass.TransparentForward,
            (int)EDefaultRenderPass.OnTopForward,
        ];

        public IReadOnlyList<int> RenderPasses { get; set; } = DefaultRenderPasses;

        private bool _gpuDispatch = false;
        public bool GPUDispatch
        {
            get => _gpuDispatch;
            set => SetField(ref _gpuDispatch, value);
        }

        public void SetOptions(bool gpuDispatch, IReadOnlyList<int>? renderPasses = null)
        {
            GPUDispatch = gpuDispatch;
            if (renderPasses is not null)
                RenderPasses = renderPasses;
        }

        protected override void Execute()
        {
            if (ParentPipeline is not DefaultRenderPipeline pipeline)
                return;

            if (RenderPasses.Count == 0)
                return;

            var material = pipeline.GetMotionVectorsMaterial();
            if (material is null)
                return;

            using var overrideTicket = ActivePipelineInstance.RenderState.PushOverrideMaterial(material);

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
                return;

            foreach (int pass in RenderPasses)
            {
                if (_gpuDispatch)
                    commands.RenderGPU(pass);
                else
                    commands.RenderCPU(pass);
            }
        }
    }
}
