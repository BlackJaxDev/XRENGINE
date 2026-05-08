using System.Collections.Generic;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderMotionVectorsPass : ViewportRenderCommand
    {
        private static readonly int[] DefaultRenderPasses =
        [
            (int)EDefaultRenderPass.Background,
            (int)EDefaultRenderPass.OpaqueDeferred,
            (int)EDefaultRenderPass.DeferredDecals,
            (int)EDefaultRenderPass.OpaqueForward,
            (int)EDefaultRenderPass.MaskedForward,
            (int)EDefaultRenderPass.WeightedBlendedOitForward,
            (int)EDefaultRenderPass.TransparentForward,
            (int)EDefaultRenderPass.OnTopForward,
        ];

        public int[] RenderPasses { get; set; } = DefaultRenderPasses;

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
                RenderPasses = [.. renderPasses];
        }

        protected override bool ShouldExecuteThisFrame()
        {
            if (Engine.Rendering.State.IsSceneCapturePass || RenderPasses.Length == 0)
                return false;

            XRRenderPipelineInstance? activeInstance = Engine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
                return false;

            for (int i = 0; i < RenderPasses.Length; i++)
            {
                if (activeInstance.MeshRenderCommands.HasRenderingCommands(RenderPasses[i]))
                    return true;
            }

            return false;
        }

        protected override void Execute()
        {
            // Scene captures (light probes, reflection probes) don't need motion vectors.
            if (Engine.Rendering.State.IsSceneCapturePass)
                return;

            XRMaterial? material = ParentPipeline switch
            {
                DefaultRenderPipeline pipeline => pipeline.GetMotionVectorsMaterial(),
                DefaultRenderPipeline2 pipeline => pipeline.GetMotionVectorsMaterial(),
                _ => null,
            };

            if (material is null)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: parent pipeline missing, wrong type, or material unavailable.");
                return;
            }

            if (RenderPasses.Length == 0)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: no render passes configured.");
                return;
            }

            var rs = ActivePipelineInstance.RenderState;

            //Debug.Out($"[Velocity] Motion vectors begin. GPU={GPUDispatch} PassCount={RenderPasses.Count}");
            using var overrideTicket = rs.PushOverrideMaterial(material);
            // Rasterize the motion-vector pass against the same unjittered projection
            // used for the reprojection uniforms so coverage and depth testing line up.
            using var unjitteredProjectionTicket = rs.PushUnjitteredProjection();
            // Force shader pipeline mode so the override material is actually used.
            // In combined shader mode, material overrides are ignored and meshes render with their original shaders.
            using var pipelineTicket = rs.PushForceShaderPipelines();
            // Motion vectors require the engine-generated mesh vertex varyings (notably FragPosLocal).
            // Some custom material vertex shaders do not emit those varyings, which leaves the velocity pass blank.
            using var generatedVertexTicket = rs.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: no mesh render commands available.");
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
