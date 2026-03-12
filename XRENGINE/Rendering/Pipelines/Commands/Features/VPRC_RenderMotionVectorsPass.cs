using System.Collections.Generic;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using Silk.NET.OpenGL;

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
            (int)EDefaultRenderPass.MaskedForward,
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
            {
                Debug.Out("[Velocity] Motion vectors pass skipped: parent pipeline missing or wrong type.");
                return;
            }

            if (RenderPasses.Count == 0)
            {
                Debug.Out("[Velocity] Motion vectors pass skipped: no render passes configured.");
                return;
            }

            var material = pipeline.GetMotionVectorsMaterial();
            if (material is null)
            {
                Debug.Out("[Velocity] Motion vectors pass skipped: motion vector material unavailable.");
                return;
            }

            //Debug.Out($"[Velocity] Motion vectors begin. GPU={GPUDispatch} PassCount={RenderPasses.Count}");
            using var overrideTicket = ActivePipelineInstance.RenderState.PushOverrideMaterial(material);
            // Force shader pipeline mode so the override material is actually used.
            // In combined shader mode, material overrides are ignored and meshes render with their original shaders.
            using var pipelineTicket = ActivePipelineInstance.RenderState.PushForceShaderPipelines();
            // Motion vectors require the engine-generated mesh vertex varyings (notably FragPosLocal).
            // Some custom material vertex shaders do not emit those varyings, which leaves the velocity pass blank.
            using var generatedVertexTicket = ActivePipelineInstance.RenderState.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
            {
                Debug.Out("[Velocity] Motion vectors pass skipped: no mesh render commands available.");
                return;
            }

            foreach (int pass in RenderPasses)
            {
                //Debug.Out($"[Velocity] Rendering motion vectors for pass {pass} (GPUDispatch={GPUDispatch}).");
                if (_gpuDispatch)
                    commands.RenderGPU(pass);
                else
                    commands.RenderCPU(pass);
            }

            //Debug.Out("[Velocity] Motion vectors end.");
        }
    }
}
